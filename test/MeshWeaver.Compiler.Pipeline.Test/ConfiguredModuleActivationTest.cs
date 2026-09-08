#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The <c>Modules:Assemblies</c> baseline rule, pinned on the pure half so it needs no filesystem
/// and no mesh — which matters because the situation it protects is exactly the one that is hardest
/// to stand up: a host whose image no longer ships something its configuration still lists.
///
/// <para>Voice is the reason this exists. <c>Memex.LocalMesh</c> stopped referencing
/// MeshWeaver.Speech and installs it as a module, so "the module is not there" changed from a
/// compile error into a runtime condition — and the only acceptable behaviour is to start without
/// voice and say so. On 3.0.0-rc5 the other answer took every portal down before anything
/// served.</para>
///
/// <para>🚨 That silence is safe ONLY for a module nobody declared required. The loud half is
/// <see cref="MeshBuilderModuleActivation.MissingRequired"/>, pinned below: once modules began
/// leaving the image for the registry, "skip quietly" alone would let a rollout drop a feature
/// with nothing failing.</para>
/// </summary>
public class ConfiguredModuleActivationTest
{
    private static string Resolve(string entry) => $"/app/{entry}";

    [Fact]
    public void APresentModule_IsInstalled_AndNothingIsSkipped()
    {
        var skips = new List<string>();

        var resolved = MeshBuilderModuleActivation.ResolveInstallable(
            ["MeshWeaver.Speech.dll"], Resolve, _ => true, skips.Add);

        Assert.Equal(["/app/MeshWeaver.Speech.dll"], resolved);
        Assert.Empty(skips);
    }

    [Fact]
    public void AListedButAbsentModule_IsSkippedLoudly_NeverThrown()
    {
        var skips = new List<string>();

        // No throw is the assertion: InstallAssemblies would do Assembly.LoadFrom and take the host
        // down, so the entry must be dropped BEFORE it gets there.
        var resolved = MeshBuilderModuleActivation.ResolveInstallable(
            ["MeshWeaver.Speech.dll"], Resolve, _ => false, skips.Add);

        Assert.Empty(resolved);
        var skip = Assert.Single(skips);
        // The line has to name the entry AND where it looked — a skip nobody can act on is noise.
        Assert.Contains("MeshWeaver.Speech.dll", skip);
        Assert.Contains("/app/MeshWeaver.Speech.dll", skip);
    }

    [Fact]
    public void OneAbsentEntry_DoesNotCostTheOthers()
    {
        var skips = new List<string>();

        var resolved = MeshBuilderModuleActivation.ResolveInstallable(
            ["Gone.dll", "MeshWeaver.Speech.dll"],
            Resolve,
            path => path.EndsWith("MeshWeaver.Speech.dll", StringComparison.Ordinal),
            skips.Add);

        Assert.Equal(["/app/MeshWeaver.Speech.dll"], resolved);
        Assert.Contains("Gone.dll", Assert.Single(skips));
    }

    [Fact]
    public void NoConfiguredModules_IsAQuietNoOp()
    {
        var skips = new List<string>();

        // Null (no Modules section at all) and blank entries are ordinary, not faults: a host that
        // installs nothing must not emit a diagnostic implying something went wrong.
        Assert.Empty(MeshBuilderModuleActivation.ResolveInstallable(null, Resolve, _ => true, skips.Add));
        Assert.Empty(MeshBuilderModuleActivation.ResolveInstallable(
            ["", "   ", null], Resolve, _ => true, skips.Add));
        Assert.Empty(skips);
    }

    // ───────── the LOUD half: Modules:Required ─────────

    private static IConfiguration Config(params string[] required)
        => new ConfigurationBuilder().AddInMemoryCollection(
            required.Select((entry, i) =>
                new KeyValuePair<string, string?>($"Modules:Required:{i}", entry))).Build();

    [Fact]
    public void ADeclaredRequiredModuleThatIsAbsent_IsReported()
    {
        var missing = MeshBuilderModuleActivation.MissingRequired(
            Config("MeshWeaver.Blazor.Radzen.dll"), Resolve, _ => false);

        // The whole point: this is the case that used to be a stderr line and a green rollout.
        Assert.Equal(["MeshWeaver.Blazor.Radzen.dll"], missing);
    }

    [Fact]
    public void ARequiredModuleThatIsPresent_IsNotReported()
    {
        Assert.Empty(MeshBuilderModuleActivation.MissingRequired(
            Config("MeshWeaver.Blazor.Radzen.dll"), Resolve, _ => true));
    }

    [Fact]
    public void DeclaringNothingRequired_IsInert()
    {
        // Today's deployments declare none, and they must behave exactly as they do now — a gate
        // that fires by default would fail every portal on the first boot after it ships.
        Assert.Empty(MeshBuilderModuleActivation.MissingRequired(Config(), Resolve, _ => false));
    }

    [Fact]
    public void RequiredIsINDEPENDENT_OfWhatIsListedUnderAssemblies()
    {
        // A module can be required without being in the baseline list (the store installed it) and
        // listed without being required (an optional pack). Neither key implies the other, so the
        // rule reads Modules:Required and nothing else.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Modules:Assemblies:0"] = "SomethingElse.dll",
            ["Modules:Required:0"] = "MeshWeaver.Speech.dll",
        }).Build();

        Assert.Equal(["MeshWeaver.Speech.dll"],
            MeshBuilderModuleActivation.MissingRequired(config, Resolve, _ => false));
    }

    // ───────── the SILENT half: an override that REPLACES a requirement ─────────

    /// <summary>
    /// The layering a deployment actually has: the image's own appsettings baseline first, then the
    /// container environment the ConfigMap supplies. Two providers, in that order — which is the
    /// whole mechanism, since the later one wins per KEY and a key here is an array INDEX.
    /// </summary>
    private static IConfiguration ImagePlusOverlay(
        IEnumerable<string> baseline, IDictionary<string, string?> overlay)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(baseline.Select((entry, i) =>
                new KeyValuePair<string, string?>($"Modules:Required:{i}", entry)))
            .AddInMemoryCollection(overlay)
            .Build();

    /// <summary>The image's list as it stands: seven entries, Social at index 5.</summary>
    private static readonly string[] ImageBaseline =
    [
        "MeshWeaver.Blazor.Radzen.dll",
        "MeshWeaver.Blazor.Analysis.dll",
        "MeshWeaver.Blazor.EntityViews.dll",
        "MeshWeaver.Blazor.GoogleMaps.dll",
        "MeshWeaver.Speech.dll",
        "MeshWeaver.Social.dll",
        "MeshWeaver.AI.dll",
    ];

    [Fact]
    public void AnOverlayEntryThatOverwritesABaselineRequirement_IsReported()
    {
        // The literal Memex#131 shape: the overlay restates 0..4 and then adds MCP "at the end" —
        // except index 5 is not the end, it is MeshWeaver.Social.dll. Nothing else in the system
        // can see this: Social is not MISSING, it is no longer REQUIRED.
        var config = ImagePlusOverlay(ImageBaseline, new Dictionary<string, string?>
        {
            ["Modules:Required:0"] = "MeshWeaver.Blazor.Radzen.dll",
            ["Modules:Required:1"] = "MeshWeaver.Blazor.Analysis.dll",
            ["Modules:Required:2"] = "MeshWeaver.Blazor.EntityViews.dll",
            ["Modules:Required:3"] = "MeshWeaver.Blazor.GoogleMaps.dll",
            ["Modules:Required:4"] = "MeshWeaver.Speech.dll",
            ["Modules:Required:5"] = "MeshWeaver.Mcp.dll",
        });

        Assert.Equal(["MeshWeaver.Social.dll"], MeshBuilderModuleActivation.ShadowedRequired(config));

        // And the guard it is NOT: every entry still resolves, so the loud half says nothing.
        Assert.Empty(MeshBuilderModuleActivation.MissingRequired(config, Resolve, _ => true));
    }

    [Fact]
    public void AnOverlayEntryPastTheBaseline_AddsWithoutReplacing()
    {
        // The fix, pinned: index 7 is the first free slot, so MCP is required IN ADDITION.
        var config = ImagePlusOverlay(ImageBaseline, new Dictionary<string, string?>
        {
            ["Modules:Required:7"] = "MeshWeaver.Mcp.dll",
        });

        Assert.Empty(MeshBuilderModuleActivation.ShadowedRequired(config));
    }

    [Fact]
    public void BlankingAnEntry_IsNotAShadow()
    {
        // Blanking is the SANCTIONED way to drop a requirement a deployment cannot satisfy — it was
        // the only remedy the 2026-08-23 rollouts had. Reporting it would make the check noise on
        // every install that legitimately opts out.
        var config = ImagePlusOverlay(ImageBaseline, new Dictionary<string, string?>
        {
            ["Modules:Required:0"] = "",
            ["Modules:Required:5"] = "",
        });

        Assert.Empty(MeshBuilderModuleActivation.ShadowedRequired(config));
    }

    [Fact]
    public void ReorderingTheList_IsNotAShadow()
    {
        // Every baseline module is still required, at a different index. Nothing was lost, so
        // nothing is reported — the check is about the SET, not the positions.
        var config = ImagePlusOverlay(ImageBaseline, new Dictionary<string, string?>
        {
            ["Modules:Required:0"] = "MeshWeaver.Social.dll",
            ["Modules:Required:5"] = "MeshWeaver.Blazor.Radzen.dll",
        });

        Assert.Empty(MeshBuilderModuleActivation.ShadowedRequired(config));
    }

    [Fact]
    public void AnUnlayeredConfiguration_HasNothingToShadow()
    {
        // One source, no overrides — today's default, and it must stay silent.
        Assert.Empty(MeshBuilderModuleActivation.ShadowedRequired(Config(ImageBaseline)));
    }

    // ───────── #3649: a generation that cannot load here falls back to the previous one ─────────
    //
    // Rule R1 of the module adoption policy (maintainer, 2026-09-07): an installation runs the
    // newest generation of every module that LOADS, and keeps the one it has until a newer one
    // does. Before this, a landed generation the link probe refused left the module ABSENT unless
    // the image shipped a baseline copy — a Store-only module has none — and the shelved landing
    // overwrote the only reference to the loadable generation, which the next GC pass then took
    // away. Every test here lands REAL assemblies through the REAL landing service, boots the way
    // MemexConfiguration.ConfigureMemexMesh boots, and drives the REAL loader.

    /// <summary>The platform assembly the stand-in impersonates — a real one this process has
    /// loaded, so the probe measures against the copy a module would actually bind to.</summary>
    private const string ContractAssembly = "MeshWeaver.Mesh.Contract";

    /// <summary>A type no build of <see cref="ContractAssembly"/> has ever carried: what a module
    /// compiled against a platform three days ahead links to.</summary>
    private const string FutureType = "MeshWeaver.Mesh.CodeOutputCurrencyFromTheFuture";

    private const string PackagePath = "Plugins/fallback-pack";

    /// <summary>
    /// 🚨 THE repro of #3649. A loadable generation is landed, then a generation built for a NEWER
    /// platform is shelved over it (the registry lane, where an unloadable module holds instead
    /// of refusing). Boot refuses the shelved generation, loads the previous one, and says so —
    /// and the module is PRESENT, never incompatible.
    /// </summary>
    [Fact]
    public async Task WhenTheNewestGenerationCannotLoadHere_ThePreviousOneLoads_AndIsReported()
    {
        using var deployment = new Deployment();
        var a = await Land(deployment, ModuleBuiltAgainstThisPlatform(deployment.Module), "1.2.3");
        var b = await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.0");

        // The landing kept the displaced generation as the fallback, with its version.
        var entry = Entry(deployment);
        Assert.Equal(b, entry.Directory);
        Assert.Equal(a, entry.PreviousDirectory);
        Assert.Equal("1.2.3", entry.PreviousVersion);

        var (services, _) = Boot(deployment);

        var installed = Assert.Single(services.GetServices<InstalledModuleAssembly>(),
            m => string.Equals(m.Assembly.GetName().Name, deployment.Module, StringComparison.Ordinal));
        Assert.Equal(a, Path.GetFileName(Path.GetDirectoryName(installed.Assembly.Location)));

        var fallback = Assert.Single(services.GetServices<FallbackModule>());
        Assert.Equal(deployment.Module, fallback.Name);
        Assert.Equal(b, fallback.Generation);
        Assert.Equal(a, fallback.PreviousGeneration);
        Assert.Equal("1.3.0", fallback.Version);
        Assert.Equal("1.2.3", fallback.PreviousVersion);
        // The report names both generations and the type the newest one wanted — the sentence the
        // incident was missing.
        Assert.StartsWith(
            $"'{deployment.Module}' runs its previous generation v1.2.3 ({a}) because v1.3.0 ({b}) "
            + "cannot load here:", fallback.Report(), StringComparison.Ordinal);
        Assert.Contains(FutureType, fallback.Reason, StringComparison.Ordinal);

        // 🚨 Present, not incompatible: nothing registers the module as degraded.
        Assert.Empty(services.GetServices<IncompatibleModule>());
    }

    /// <summary>
    /// 🚨 <b>#3650, the whole chain.</b> #3665 keeps the previous generation running but writes
    /// nothing the update reconcile reads, so a deployment in fallback answered "already landed"
    /// for exactly the build that would have got it off its previous generation. This walks the
    /// seam end to end, through the REAL landing, the REAL boot composition and the REAL decision:
    /// a build for a newer platform is shelved over a loadable one ⇒ the boot refuses it, runs the
    /// previous generation, and WRITES THE MARKER (the head's generation and identity) ⇒ the entry
    /// reads back in fallback ⇒ an index entry with the SAME version and a DIFFERENT identity than
    /// the refused build is <c>Land</c>, the refused build itself is <c>SkipUnloadable</c> ⇒ a
    /// build that loads lands (the marker for the old head is inert at once, without a write) ⇒
    /// the boot loads it and CLEARS the marker ⇒ the same served identity is <c>SkipUpToDate</c>.
    /// </summary>
    [Fact]
    public async Task TheBootThatFallsBack_WritesTheMarkerTheReconcileReExamines_AndTheBootThatLoads_ClearsIt()
    {
        const string Loadable = "s1111111111111111111111111111111a";
        const string Future = "s2222222222222222222222222222222b";
        const string Rebuilt = "s3333333333333333333333333333333c";
        const string Version = "1.4.0";
        using var deployment = new Deployment();
        string? Gate(string? floor) => ModulePlatformFloor.DeclineReason(floor, "3.2.0");
        bool Present(ModuleActivationEntry e) => ModuleActivationBoot.LandedModuleDllExists(deployment.Root, e);
        ModuleUpdateVerdict Decide(string served) => ModuleUpdateDecision.Decide(
            Version, bundleMinMeshVersion: null, Gate, Entry(deployment), policyDecline: null, Present, served);
        // 🚨 ONE assembly for both loadable generations. This process boots the deployment three
        // times, and its load context can hold ONE assembly per simple name: a second, different
        // build of the same name is refused with "Assembly with same name is already loaded" — a
        // constraint of the test process (every real boot is a fresh one), not of the loader. The
        // identity the reconcile compares is the string the REGISTRY advertises, recorded on the
        // entry at landing (Loadable / Rebuilt below), not the bytes' MVID — so the same bytes
        // landed under a different advertised identity walk the chain faithfully.
        var loadable = ModuleBuiltAgainstThisPlatform(deployment.Module);

        // ── 1. A loadable generation runs; nothing is marked ───────────────────────────────────
        var a = await Land(deployment, loadable, Version, Loadable);
        Boot(deployment);
        Assert.Null(Entry(deployment).UnloadableFrameworkMvid);
        Assert.False(File.Exists(ModuleActivationSidecar.UnloadableMarkerPath(deployment.Root, deployment.Module)));
        Assert.Equal(ModuleUpdateAction.SkipUpToDate, Decide(Loadable).Action);

        // ── 2. The same version, rebuilt for a NEWER platform, is shelved over it ──────────────
        var b = await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), Version, Future);
        var (services, _) = Boot(deployment);
        var fallback = Assert.Single(services.GetServices<FallbackModule>());
        Assert.Equal(b, fallback.Generation);
        Assert.Equal(a, fallback.PreviousGeneration);

        // The boot WROTE the marker: the head's generation and identity, and why.
        var marker = ModuleActivationSidecar.ReadUnloadable(deployment.Root, deployment.Module);
        Assert.NotNull(marker);
        Assert.Equal(b, marker!.Generation);
        Assert.Equal(Future, marker.FrameworkMvid);
        Assert.Contains(FutureType, marker.Reason, StringComparison.Ordinal);

        // …and the entry reads back IN FALLBACK — which is what the decision consumes.
        var inFallback = Entry(deployment);
        Assert.Equal(b, inFallback.Directory);
        Assert.Equal(Future, inFallback.UnloadableFrameworkMvid);

        // ── 3. The decision: a different build of this version lands; the refused one does not ─
        var lands = Decide(Rebuilt);
        Assert.Equal(ModuleUpdateAction.Land, lands.Action);
        Assert.Contains(Future, lands.Reason);
        Assert.Contains(Rebuilt, lands.Reason);
        var stays = Decide(Future);
        Assert.Equal(ModuleUpdateAction.SkipUnloadable, stays.Action);
        Assert.DoesNotContain("already landed", stays.Reason);

        // ── 4. The build for this platform lands (what the reconcile now does) ─────────────────
        var c = await Land(deployment, loadable, Version, Rebuilt);
        Assert.NotEqual(b, c);
        // Before any boot the old marker still exists — and is INERT: it measured b, the head is c.
        Assert.NotNull(ModuleActivationSidecar.ReadUnloadable(deployment.Root, deployment.Module));
        Assert.Null(Entry(deployment).UnloadableFrameworkMvid);
        Assert.Equal(ModuleUpdateAction.SkipUpToDate, Decide(Rebuilt).Action);

        // ── 5. The boot loads the new head and CLEARS the marker ───────────────────────────────
        var (afterwards, _) = Boot(deployment);
        Assert.Empty(afterwards.GetServices<FallbackModule>());
        Assert.Empty(afterwards.GetServices<IncompatibleModule>());
        Assert.Null(ModuleActivationSidecar.ReadUnloadable(deployment.Root, deployment.Module));
        Assert.Null(Entry(deployment).UnloadableFrameworkMvid);
        Assert.Equal(ModuleUpdateAction.SkipUpToDate, Decide(Rebuilt).Action);
        // The ordinary identity rule is back in charge: yet another build still lands.
        Assert.Equal(ModuleUpdateAction.Land, Decide(Loadable).Action);
    }

    /// <summary>
    /// When NO generation loads, the head is unloadable too — and the marker says so, so a build
    /// for this platform still lands the moment one ships (the module is parked, not forgotten).
    /// </summary>
    [Fact]
    public async Task WhenNoGenerationLoadsHere_TheHeadIsStillMarkedUnloadable()
    {
        const string Future = "s4444444444444444444444444444444d";
        using var deployment = new Deployment();
        await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.0");
        var head = await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.0", Future);

        var (services, _) = Boot(deployment);

        Assert.Single(services.GetServices<IncompatibleModule>());
        var marker = ModuleActivationSidecar.ReadUnloadable(deployment.Root, deployment.Module);
        Assert.NotNull(marker);
        Assert.Equal(head, marker!.Generation);
        Assert.Equal(Future, Entry(deployment).UnloadableFrameworkMvid);
    }

    /// <summary>
    /// The other half of "as today": when NO generation loads here, the module is incompatible
    /// exactly as it was before #3649 — named, refused before load, contributing nothing.
    /// </summary>
    [Fact]
    public async Task WhenNoGenerationLoadsHere_TheModuleIsIncompatible_AsBefore()
    {
        using var deployment = new Deployment();
        var first = await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.0");
        await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.1");
        Assert.Equal(first, Entry(deployment).PreviousDirectory);

        var (services, _) = Boot(deployment);

        Assert.Empty(services.GetServices<FallbackModule>());
        var parked = Assert.Single(services.GetServices<IncompatibleModule>());
        Assert.Equal(deployment.Module, parked.Name);
        Assert.True(parked.RefusedBeforeLoad);
        Assert.Contains(FutureType, parked.Report(), StringComparison.Ordinal);
        Assert.DoesNotContain(services.GetServices<InstalledModuleAssembly>(),
            m => string.Equals(m.Assembly.GetName().Name, deployment.Module, StringComparison.Ordinal));
    }

    /// <summary>
    /// 🚨 Two unloadable landings in a row must not push the loadable generation out of reach. The
    /// landing MEASURES the generation it displaces: when that one cannot load here and holds a
    /// fallback of its own, the fallback carries forward — so the entry keeps pointing at the one
    /// generation that runs, and boot runs it.
    /// </summary>
    [Fact]
    public async Task TwoUnloadableLandingsInARow_KeepTheLoadableGenerationAsTheFallback()
    {
        using var deployment = new Deployment();
        var a = await Land(deployment, ModuleBuiltAgainstThisPlatform(deployment.Module), "1.2.3");
        await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.0");
        var c = await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.1");

        var entry = Entry(deployment);
        Assert.Equal(c, entry.Directory);
        Assert.Equal(a, entry.PreviousDirectory);
        Assert.Equal("1.2.3", entry.PreviousVersion);

        var (services, _) = Boot(deployment);
        Assert.Equal(a, Assert.Single(services.GetServices<FallbackModule>()).PreviousGeneration);
    }

    /// <summary>
    /// 🚨 The fallback generation is REFERENCED: the GC keeps it while it is the one that loads.
    /// Reclaiming it is precisely how a shelved landing used to take a working module away for
    /// good. The negative control — an uninstall clears the references and both go — is what
    /// keeps this from passing by never collecting anything.
    /// </summary>
    [Fact]
    public async Task GarbageCollection_KeepsTheFallbackGeneration()
    {
        using var deployment = new Deployment();
        var a = await Land(deployment, ModuleBuiltAgainstThisPlatform(deployment.Module), "1.2.3");
        var b = await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.0");

        ModuleLandingService.CollectGarbage(deployment.Root, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));

        Assert.True(Directory.Exists(deployment.GenerationDirectory(a)),
            "the previous generation — the only one that loads here — was reclaimed");
        Assert.True(Directory.Exists(deployment.GenerationDirectory(b)));

        await deployment.Landing.RemoveModule(deployment.Module).Timeout(TestTimeouts.Convergence).Await();
        ModuleLandingService.CollectGarbage(deployment.Root, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));

        Assert.False(Directory.Exists(deployment.GenerationDirectory(a)));
        Assert.False(Directory.Exists(deployment.GenerationDirectory(b)));
    }

    /// <summary>
    /// The mesh's set still PROPOSES the head generation — that is what the wave landed — but the
    /// ADOPTION records what the replica actually loaded, so a reader of the set records learns
    /// what runs, the GC references it, and the mesh-set line says so.
    /// </summary>
    [Fact]
    public async Task TheAdoptedSet_RecordsTheGenerationThatActuallyLoaded()
    {
        using var deployment = new Deployment();
        var a = await Land(deployment, ModuleBuiltAgainstThisPlatform(deployment.Module), "1.2.3");
        var b = await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.0");
        var proposed = await deployment.Landing.ProposeModuleSet().Timeout(TestTimeouts.Convergence).Await();
        Assert.NotNull(proposed);
        Assert.Equal(b, proposed.Generations[deployment.Module]);

        Boot(deployment);

        var index = ModuleSetStore.Read(deployment.Root);
        Assert.NotNull(index.Current);
        Assert.False(index.ConvergencePending);
        Assert.Equal(b, index.Current.Generations[deployment.Module]);
        Assert.Equal(a, index.RunningGenerations[deployment.Module]);
        Assert.Equal(a, Assert.Single(index.FallbackGenerations).Value);
        Assert.Contains(a, ModuleSetStore.ReferencedGenerations(index));
        Assert.Contains("PREVIOUS generation", ModuleSetStore.Describe(index), StringComparison.Ordinal);
        Assert.Contains(a, ModuleSetStore.Describe(index), StringComparison.Ordinal);

        // …and the GC, which reads the set records, keeps it on that evidence alone.
        ModuleLandingService.CollectGarbage(deployment.Root, minAge: TimeSpan.Zero, nowUtc: DateTime.UtcNow.AddHours(1));
        Assert.True(Directory.Exists(deployment.GenerationDirectory(a)));
    }

    /// <summary>An uninstall clears BOTH pointers: a disabled entry must not keep either
    /// generation referenced, or the GC could never reclaim them.</summary>
    [Fact]
    public async Task Uninstall_ClearsBothGenerationPointers()
    {
        using var deployment = new Deployment();
        var a = await Land(deployment, ModuleBuiltAgainstThisPlatform(deployment.Module), "1.2.3");
        var b = await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.0");

        await deployment.Landing.RemoveModule(deployment.Module).Timeout(TestTimeouts.Convergence).Await();

        var entry = Entry(deployment);
        Assert.False(entry.Enabled);
        Assert.Null(entry.Directory);
        Assert.Null(entry.PreviousDirectory);
        Assert.Null(entry.PreviousVersion);
        Assert.Null(entry.PreviousFrameworkMvid);
        Assert.False(Directory.Exists(deployment.GenerationDirectory(a)));
        Assert.False(Directory.Exists(deployment.GenerationDirectory(b)));
    }

    /// <summary>
    /// The projection onto the mesh's set carries the fallback pointer with the entry, and drops
    /// it only when it names the set's own generation (the mid-wave shape: the entry moved to D
    /// with the set's G as its fallback — G is the head now and needs no fallback to itself).
    /// </summary>
    [Fact]
    public void TheProjectionOntoTheMeshSet_CarriesTheFallbackPointer_UnlessItNamesTheSetsGeneration()
    {
        const string name = "MeshWeaver.Test.Projected";
        var set = new ModuleSet(1, "set-1",
            ImmutableSortedDictionary.CreateRange(StringComparer.OrdinalIgnoreCase,
                [KeyValuePair.Create(name, name + "@g")]),
            DateTime.UtcNow);

        static ModuleActivationList Record(string head, string previous) => new()
        {
            Entries =
            [
                new ModuleActivationEntry
                {
                    Name = name, Directory = head, PreviousDirectory = previous, PreviousVersion = "1.0.0",
                },
            ],
        };

        var midWave = ModuleActivationBoot.ProjectOntoMeshSet(Record(name + "@d", name + "@g"), set)
            .Entries.Single();
        Assert.Equal(name + "@g", midWave.Directory);
        Assert.Null(midWave.PreviousDirectory);
        Assert.Null(midWave.PreviousVersion);

        var carried = ModuleActivationBoot.ProjectOntoMeshSet(Record(name + "@d", name + "@a"), set)
            .Entries.Single();
        Assert.Equal(name + "@g", carried.Directory);
        Assert.Equal(name + "@a", carried.PreviousDirectory);
        Assert.Equal("1.0.0", carried.PreviousVersion);
    }

    /// <summary>
    /// A module running its previous generation is a NAMED ROW on the activation report, never
    /// "restart required" (a restart re-measures the same bytes and falls back again) and never
    /// "not running here" (it is running). Without the loader's record the same state reads as an
    /// ordinary pending update — the false prompt the record exists to remove — and once the
    /// record has moved past the refused generation, a restart genuinely tries something new and
    /// the module is pending again.
    /// </summary>
    [Fact]
    public async Task TheActivationReport_NamesAFallback_AndNeverCallsItRestartRequired()
    {
        using var deployment = new Deployment();
        var a = await Land(deployment, ModuleBuiltAgainstThisPlatform(deployment.Module), "1.2.3");
        var b = await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.0");
        var (services, _) = Boot(deployment);
        var fallbacks = services.GetServices<FallbackModule>().ToArray();
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { deployment.Module };
        var generations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [deployment.Module] = a,
        };

        var report = new PendingModuleActivations(deployment.Root) { FallbackModules = fallbacks }
            .Read(loaded, generations);

        Assert.False(report.IsUndetermined, report.UndeterminedReason);
        Assert.True(report.HasFallbacks);
        var row = Assert.Single(report.Fallbacks);
        Assert.Equal(deployment.Module, row.Name);
        Assert.Equal(PackagePath, row.PackagePath);
        Assert.Equal(a, row.PreviousGeneration);
        Assert.Equal(b, row.Generation);
        Assert.Equal("1.2.3", row.PreviousVersion);
        Assert.Equal("1.3.0", row.Version);
        Assert.StartsWith($"runs v1.2.3 ({a}); v1.3.0 ({b}) landed but does not load here:",
            row.Reason, StringComparison.Ordinal);
        Assert.Same(row, report.FallbackForPackage(PackagePath));
        Assert.Null(report.FallbackForPackage(null));
        Assert.False(report.HasPending, "a restart re-measures the same bytes and falls back again");
        Assert.False(report.IsPendingForPackage(PackagePath));
        Assert.False(report.HasQuarantined, "it is running");
        Assert.Contains("PREVIOUS generation", report.Describe(), StringComparison.Ordinal);

        var blind = new PendingModuleActivations(deployment.Root).Read(loaded, generations);
        Assert.True(blind.HasPending, "without the loader's record the state reads as an ordinary update");

        await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.1");
        var movedOn = new PendingModuleActivations(deployment.Root) { FallbackModules = fallbacks }
            .Read(loaded, generations);
        Assert.True(movedOn.HasPending, "the record moved past the refused generation — a restart tries the new one");
        Assert.True(movedOn.HasFallbacks);
    }

    /// <summary>
    /// A REQUIRED module running its previous generation is <see cref="RequiredModuleState.Present"/>:
    /// the readiness probe stays Healthy on a fallback. Classified off the real loader's output —
    /// the loaded set and the incompatible set it registered — so the verdict cannot agree with
    /// itself while the loader diverges.
    /// </summary>
    [Fact]
    public async Task ARequiredModuleRunningItsPreviousGeneration_IsPresent()
    {
        using var deployment = new Deployment();
        await Land(deployment, ModuleBuiltAgainstThisPlatform(deployment.Module), "1.2.3");
        await Shelve(deployment, ModuleBuiltAgainstAFuturePlatform(deployment.Module), "1.3.0");
        var (services, _) = Boot(deployment);

        var verdicts = RequiredModuleStatus.Classify(
            requiredEntries: [deployment.Module + ".dll"],
            baselineEntries: [],
            loadedAssemblyNames: services.GetServices<InstalledModuleAssembly>()
                .Select(m => m.Assembly.GetName().Name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            resolvesFromDeployment: _ => false,
            activation: ModuleActivationSidecar.Read(deployment.Root),
            landedDllExists: entry => ModuleActivationBoot.LandedModuleDllExists(deployment.Root, entry),
            platformGate: _ => null,
            incompatibleModules: [.. services.GetServices<IncompatibleModule>()]);

        var verdict = Assert.Single(verdicts);
        Assert.Equal(deployment.Module, verdict.Name);
        Assert.Equal(RequiredModuleState.Present, verdict.State);
        Assert.Empty(RequiredModuleStatus.Incompatible(verdicts));
        Assert.Empty(RequiredModuleStatus.ExpectedLater(verdicts));
    }

    // ───────────────────────────────────────────────────────────── harness

    /// <summary>A per-test deployment root with the REAL landing service over it. The module name
    /// is unique per test: the loader puts what it loads into the default load context, and two
    /// different assemblies of one simple name cannot both live there.</summary>
    private sealed class Deployment : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "mw-fallback-" + Guid.NewGuid().ToString("N"));

        public string PinRoot => Path.Combine(Root, "pinned");

        public string Module { get; } = "MeshWeaver.Test.Fallback" + Guid.NewGuid().ToString("N")[..8];

        public ModuleLandingService Landing { get; }

        public Deployment()
        {
            Directory.CreateDirectory(Root);
            Landing = new ModuleLandingService(baseDirectory: Root);
        }

        public string GenerationDirectory(string generation) => Path.Combine(Root, "modules", generation);

        public void Dispose()
        {
            Landing.Dispose();
            try { Directory.Delete(Root, recursive: true); }
            catch { /* temp cleanup is the OS's problem, never a test failure */ }
        }
    }

    private static ModuleActivationEntry Entry(Deployment deployment) =>
        ModuleActivationSidecar.Read(deployment.Root).Entries
            .Single(e => string.Equals(e.Name, deployment.Module, StringComparison.Ordinal));

    /// <summary>Lands a generation on the ADOPT path (refuses what cannot load) and returns its
    /// generation directory leaf. <paramref name="frameworkMvid"/> is the identity the registry
    /// would advertise for these bytes — what the update reconcile compares (#3650).</summary>
    private static async Task<string> Land(
        Deployment deployment, byte[] bytes, string version, string? frameworkMvid = null)
    {
        await deployment.Landing.LandModule(
                deployment.Module, [(deployment.Module + ".dll", bytes)],
                frameworkMvid: frameworkMvid, packagePath: PackagePath, version: version)
            .Timeout(TestTimeouts.Convergence).Await();
        return Entry(deployment).Directory!;
    }

    /// <summary>Lands a generation on the SHELF path (holds what cannot load) and returns its
    /// generation directory leaf. Asserts it was held — every shelved generation here is one
    /// built for a newer platform.</summary>
    private static async Task<string> Shelve(
        Deployment deployment, byte[] bytes, string version, string? frameworkMvid = null)
    {
        var outcome = await deployment.Landing.ShelveModule(
                deployment.Module, [(deployment.Module + ".dll", bytes)],
                frameworkMvid: frameworkMvid, packagePath: PackagePath, version: version)
            .Timeout(TestTimeouts.Convergence).Await();
        Assert.True(outcome.Held, "the shelved generation was expected to be unloadable here");
        return Entry(deployment).Directory!;
    }

    /// <summary>
    /// One replica's boot, composed EXACTLY as <c>MemexConfiguration.ConfigureMemexMesh</c>
    /// composes it — read the record, read the mesh's sets, project, compute the union, pin the
    /// head generation, hand the loader a LAZY pin of the previous one, install, record the
    /// adoption off what the loader registered. Re-deriving any of it here would let this test
    /// agree with itself while the portal diverges.
    /// </summary>
    private static (IServiceProvider Services, ModuleSetIndex Sets) Boot(Deployment deployment)
    {
        var root = deployment.Root;
        var persisted = ModuleActivationSidecar.Read(root);
        var sets = ModuleSetStore.Read(root);
        var onMeshSet = ModuleActivationBoot.ProjectOntoMeshSet(
            persisted,
            sets.Proposed,
            onDeferred: null,
            landedDllExists: entry => ModuleActivationBoot.LandedModuleDllExists(root, entry));
        var effective = ModuleActivationBoot.ComputeEffectiveModuleEntries(
            baselineEntries: null,
            onMeshSet,
            ModulePlatformFloor.DeclineReason,
            entry => ModuleActivationBoot.LandedModuleDllExists(root, entry));
        var candidates = effective
            .Select(module =>
            {
                var landed = module.Landed!;
                var previous = ModuleActivationBoot.PreviousGeneration(landed) is { } older
                               && ModuleActivationBoot.LandedModuleDllExists(root, older)
                    ? older
                    : null;
                return new ModuleInstallCandidate(
                    ModuleGenerationPin.PinnedLoadPath(root, landed, deployment.PinRoot))
                {
                    Version = landed.Version,
                    PreviousVersion = previous?.Version,
                    Previous = previous is null
                        ? null
                        : () => ModuleGenerationPin.PinnedLoadPath(root, previous, deployment.PinRoot),
                };
            })
            .ToArray();

        var services = new ServiceCollection();
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "test"));
        builder.InstallModules(candidates);
        var provider = services.BuildServiceProvider();

        if (sets.Proposed is { } adopted)
            ModuleSetStore.RecordAdoption(root, adopted,
                ModuleSetStore.RunningGenerationsOf(adopted, provider.GetServices<FallbackModule>()),
                adoptedBy: "replica");
        // #3650 — the boot that falls back writes the marker the reconcile re-examines: the same
        // call ModuleLoadabilityRecorder makes on the portal, off the same records, for the same
        // entries the loader was handed.
        ModuleActivationBoot.RecordMeasuredLoadability(
            root,
            effective.Select(module => module.Landed!),
            provider.GetServices<FallbackModule>(),
            provider.GetServices<IncompatibleModule>());
        return (provider, sets);
    }

    /// <summary>A module compiled against a STAND-IN <see cref="ContractAssembly"/> carrying
    /// <see cref="FutureType"/> — the memex-cloud shape: same assembly simple name as one this
    /// process has loaded, and that copy does not have the type.</summary>
    private static byte[] ModuleBuiltAgainstAFuturePlatform(string moduleName)
    {
        var future = Emit(ContractAssembly, """
            namespace MeshWeaver.Mesh;
            /// <summary>The type a newer platform has and this one does not.</summary>
            public enum CodeOutputCurrencyFromTheFuture { Unknown, Current, Stale }
            """, excludeReference: ContractAssembly);

        return Emit(moduleName, """
            using MeshWeaver.Mesh;
            public static class CodeViews
            {
                public static string BuildContent(CodeOutputCurrencyFromTheFuture currency)
                    => currency.ToString();
            }
            """,
            excludeReference: ContractAssembly,
            extra: MetadataReference.CreateFromImage(future));
    }

    /// <summary>A module compiled against the REAL contract this process runs — the generation
    /// that loads.</summary>
    private static byte[] ModuleBuiltAgainstThisPlatform(string moduleName) =>
        Emit(moduleName, """
            using MeshWeaver.Mesh;
            public static class CodeViews
            {
                public static string BuildContent(MeshNode node) => node.Id;
            }
            """);

    /// <summary>Compiles one source file into an assembly named <paramref name="assemblyName"/>,
    /// against this process's own reference set.</summary>
    private static byte[] Emit(
        string assemblyName, string source, string? excludeReference = null,
        MetadataReference? extra = null)
    {
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Where(p => excludeReference is null
                        || !string.Equals(Path.GetFileNameWithoutExtension(p), excludeReference,
                            StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        if (extra is not null)
            platform.Add(extra);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }
}
