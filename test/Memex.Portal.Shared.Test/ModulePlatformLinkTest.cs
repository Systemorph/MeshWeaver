using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A module is adopted on what it CAN LOAD, never on a version string (#3538, #3518).</b>
///
/// <para><b>The incident these reproduce.</b> memex-cloud ran a core of 2026-09-03 and adopted
/// <c>DefaultViews</c>' <c>MeshWeaver.Graph.Views</c>, whose bytes were compiled on 09-06 against a
/// <c>MeshWeaver.Mesh.Contract</c> carrying <c>CodeOutputCurrency</c> — a type added on 09-04. The
/// only gate was the module's declared <c>minMeshVersion: 3.0.0-rc8</c> floor, which the running
/// <c>3.0.0-rc9.ci.7693</c> satisfied, so the bytes landed and loaded. They installed CLEANLY —
/// the module's <c>MeshNodeProviderAttribute</c> touches none of the missing surface, so #2234's
/// per-module install isolation saw nothing — and then EVERY render of every code cell threw
/// <c>TypeLoadException: Could not load type 'MeshWeaver.Mesh.CodeOutputCurrency'</c>, for every
/// user, until a human read a pod log.</para>
///
/// <para><b>Why the declared floor can never answer it.</b> A floor is a CLAIM about API
/// compatibility that an author writes by hand. The module's real requirement is not a version at
/// all — it is the SET OF TYPES its bytes are linked against, and that set is stated exactly, by
/// the compiler, in the assembly's own metadata. So it is MEASURED against the platform copies
/// this process would actually bind to (<see cref="ModulePlatformLink"/>), metadata only, with no
/// <c>Assembly.Load</c> and no type loading.</para>
///
/// <para><b>What makes these able to FAIL.</b> Every one compiles REAL assemblies with Roslyn and
/// runs the REAL gate. The landing tests compile a module against a stand-in
/// <c>MeshWeaver.Mesh.Contract</c> that carries a type the platform running this test does NOT
/// have — the memex-cloud shape verbatim, same assembly simple name, missing type — and drive the
/// REAL <see cref="ModuleLandingService"/>. Neutralising the gate (making
/// <c>ModuleLinkVerdict.MayLoad</c> true for every state) flips
/// <see cref="AModuleBuiltAgainstANewerPlatform_IsRefusedAtLanding_NamingTheMissingType"/>,
/// <see cref="UnreadableBytes_AreRefused_NotWavedThroughAsUnknown"/> and
/// <see cref="AnUnloadableModule_IsParked_AndTheOthersStillInstall"/>. Removing the positive
/// controls' counterpart — <see cref="AModuleBuiltAgainstThisPlatform_LandsAsBefore"/> and
/// <see cref="ALinkableModule_ReportsANonZeroDenominator"/> — is what stops the gate from passing
/// by refusing everything, or by checking nothing.</para>
/// </summary>
public class ModulePlatformLinkTest : IDisposable
{
    /// <summary>The platform assembly the stand-in impersonates: a REAL one this process has
    /// loaded, so the test measures against the copy a module would actually bind to.</summary>
    private const string ContractAssembly = "MeshWeaver.Mesh.Contract";

    /// <summary>A type no build of <see cref="ContractAssembly"/> has ever carried — the stand-in
    /// for <c>CodeOutputCurrency</c> as it looked from a platform three days behind.</summary>
    private const string FutureType = "MeshWeaver.Mesh.CodeOutputCurrencyFromTheFuture";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), "mw-linkprobe-" + Guid.NewGuid().ToString("N"));

    private readonly ModuleLandingService landing;

    /// <summary>Creates the per-test deployment root and the REAL landing service over it.</summary>
    public ModulePlatformLinkTest()
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

    // ───────────────────────────────────────────────────── the floor is the BUILD platform

    /// <summary>
    /// 🚨 <b>THE repro of #3538, at the landing.</b> A module compiled against a
    /// <c>MeshWeaver.Mesh.Contract</c> that has a type this platform's copy does not is REFUSED,
    /// and the refusal NAMES the type — the sentence that was missing from the memex-cloud
    /// incident, where the only evidence was a <c>TypeLoadException</c> per render.
    ///
    /// <para>The declared floor is deliberately left absent, so the ONLY thing that can refuse
    /// this is the measured one. Before the fix, an absent floor was "no constraint" and these
    /// bytes landed.</para>
    /// </summary>
    [Fact]
    public async Task AModuleBuiltAgainstANewerPlatform_IsRefusedAtLanding_NamingTheMissingType()
    {
        var module = ModuleBuiltAgainstAFuturePlatform("MeshWeaver.Test.FutureViewPack");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await landing.LandModule(
                    "MeshWeaver.Test.FutureViewPack",
                    [("MeshWeaver.Test.FutureViewPack.dll", module)],
                    minMeshVersion: null)
                .Timeout(TestTimeouts.Convergence).Await());

        Assert.Contains(FutureType, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(ContractAssembly, refusal.Message, StringComparison.Ordinal);
        // 🚨 And nothing landed: the previous generation is what keeps serving. A refusal that
        // still wrote the bytes would leave the next boot to load them.
        Assert.Empty(ModuleActivationSidecar.Read(root).Entries);
    }

    /// <summary>
    /// The positive control, and the reason the gate above is a gate rather than a wall: a module
    /// compiled against THIS platform's real contract lands exactly as it always did.
    /// </summary>
    [Fact]
    public async Task AModuleBuiltAgainstThisPlatform_LandsAsBefore()
    {
        var module = ModuleBuiltAgainstThisPlatform("MeshWeaver.Test.CurrentViewPack");

        await landing.LandModule(
                "MeshWeaver.Test.CurrentViewPack",
                [("MeshWeaver.Test.CurrentViewPack.dll", module)])
            .Timeout(TestTimeouts.Convergence).Await();

        var entry = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("MeshWeaver.Test.CurrentViewPack", entry.Name);
        Assert.True(entry.Enabled);
    }

    /// <summary>
    /// 🚨 The SHELF path is unchanged in kind: a registry stocking a module for OTHER platforms
    /// must still carry bytes IT cannot load. They land and serve; this process's boot refuses
    /// them, which is exactly what the floor already did (2026-08-22's three-way deadlock).
    /// </summary>
    [Fact]
    public async Task AnUnloadableModule_IsShelvedRatherThanRefused_OnThePublishPath()
    {
        var module = ModuleBuiltAgainstAFuturePlatform("MeshWeaver.Test.ShelvedViewPack");

        var outcome = await landing.ShelveModule(
                "MeshWeaver.Test.ShelvedViewPack",
                [("MeshWeaver.Test.ShelvedViewPack.dll", module)])
            .Timeout(TestTimeouts.Convergence).Await();

        Assert.True(outcome.Held);
        Assert.Contains(FutureType, outcome.HoldReason!, StringComparison.Ordinal);
        // The bytes ARE on the shelf — consumers fetch them and apply this same measurement
        // against THEIR platform.
        Assert.Single(ModuleActivationSidecar.Read(root).Entries);
    }

    /// <summary>
    /// 🚨 #3996 — an older shelf-only upload is retained only when it is a useful fallback.
    /// A numerically newer fallback that this platform cannot load must not displace the working
    /// one: fallback ordering is loadability first, version second, just like activation itself.
    /// </summary>
    [Fact]
    public async Task AnOlderUnloadablePublish_DoesNotReplaceTheWorkingFallback()
    {
        const string name = "MeshWeaver.Test.OrderedShelf";
        var oldLoadable = ModuleBuiltAgainstThisPlatform(name);
        var newestLoadable = ModuleBuiltAgainstThisPlatform(name);
        var middleUnloadable = ModuleBuiltAgainstAFuturePlatform(name);

        await landing.ShelveModule(
                name, [(name + ".dll", oldLoadable)], version: "1.5.0")
            .Timeout(TestTimeouts.Convergence).Await();
        await landing.ShelveModule(
                name, [(name + ".dll", newestLoadable)], version: "1.7.0")
            .Timeout(TestTimeouts.Convergence).Await();
        var outcome = await landing.ShelveModule(
                name, [(name + ".dll", middleUnloadable)], version: "1.6.0")
            .Timeout(TestTimeouts.Convergence).Await();

        Assert.True(outcome.ShelfOnly);
        Assert.True(outcome.Held);
        Assert.False(outcome.RetainedAsFallback,
            "a working fallback is never displaced by bytes this platform measured as unloadable");
        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal("1.7.0", head.Version);
        Assert.Equal("1.5.0", head.PreviousVersion);
        var fallback = ModuleActivationBoot.PreviousGeneration(head);
        Assert.NotNull(fallback);
        Assert.True(ModulePlatformLink.Check(
            ModuleActivationBoot.LandedDllPath(root, fallback),
            ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory)).MayLoad);
    }

    /// <summary>
    /// 🚨 #3996 — a NEWER head this registry cannot load ITSELF is still the head. The shelf carries
    /// modules for platforms newer than the registry serving them, and boot runs the fallback when
    /// the head does not link here (#3649, rule R1), so an older upload that DOES link here takes the
    /// FALLBACK slot, never the head. Were loadability-here a condition on the head, 1.6.1 would
    /// become the head, and the first restart after this registry's own platform caught up would load
    /// 1.6.1 over 1.7.0 — #3996 by another road; with a loadable fallback already recorded, the newer
    /// generation would not even be kept.
    /// </summary>
    [Fact]
    public async Task ANewerHeadThatDoesNotLinkHere_StaysTheHead_AndTheOlderLoadablePublishBecomesItsFallback()
    {
        const string name = "MeshWeaver.Test.WarehousedHead";
        var first = await landing.ShelveModule(
                name, [(name + ".dll", ModuleBuiltAgainstAFuturePlatform(name))], version: "1.7.0")
            .Timeout(TestTimeouts.Convergence).Await();
        Assert.True(first.Held,
            "precondition: the 1.7.0 head does NOT link on this platform — without it this proves nothing");

        var outcome = await landing.ShelveModule(
                name, [(name + ".dll", ModuleBuiltAgainstThisPlatform(name))], version: "1.6.1")
            .Timeout(TestTimeouts.Convergence).Await();

        Assert.False(outcome.Held, "precondition: 1.6.1 links here");
        Assert.True(outcome.ShelfOnly,
            "a newer head that does not link HERE is still the newest landed generation on this shelf");
        Assert.Equal("1.7.0", outcome.HeadVersion);
        Assert.True(outcome.RetainedAsFallback);
        Assert.True(outcome.RestartRequired,
            "the fallback moved while the head does not load here, so boot runs 1.6.1 at the next restart");

        var list = ModuleActivationSidecar.Read(root);
        var head = Assert.Single(list.Entries);
        Assert.Equal("1.7.0", head.Version);
        Assert.Equal("1.6.1", head.PreviousVersion);
        Assert.True(list.PendingRestart);
    }

    /// <summary>
    /// A version that is not SemVer at all is UNKNOWN, not "older". <c>NuGetVersionComparer</c> reads
    /// an unparseable part as 0, so without the check a "nightly" label would rank below every real
    /// version and be shelved for good — a string deciding what the bytes should (rule R2).
    /// </summary>
    [Fact]
    public async Task ANonSemVerVersion_IsUnknown_AndMovesTheHeadAsBefore()
    {
        const string name = "MeshWeaver.Test.NightlyLabel";
        Assert.True(MeshWeaver.Plugin.Packaging.NuGetVersionComparer.Instance.Compare("nightly", "1.7.0") < 0,
            "precondition: the comparer ranks a non-SemVer label below every real version");
        await landing.ShelveModule(
                name, [(name + ".dll", ModuleBuiltAgainstThisPlatform(name))], version: "1.7.0")
            .Timeout(TestTimeouts.Convergence).Await();

        var outcome = await landing.ShelveModule(
                name, [(name + ".dll", ModuleBuiltAgainstThisPlatform(name))], version: "nightly")
            .Timeout(TestTimeouts.Convergence).Await();

        Assert.False(outcome.ShelfOnly,
            "a non-SemVer label is no evidence of order — it moves the head as an unversioned upload does");
        Assert.Equal("nightly", Assert.Single(ModuleActivationSidecar.Read(root).Entries).Version);
    }

    /// <summary>
    /// A head that LINKS but that the boot already failed to load is running its fallback, which a
    /// static link probe cannot see — only the boot's unloadable marker says so. A shelf-only upload
    /// that moves that fallback therefore changes what the next restart loads, and must say so.
    /// </summary>
    [Fact]
    public async Task AShelfOnlyUploadMovingTheFallback_UnderAHeadTheBootFailedToLoad_RequiresARestart()
    {
        const string name = "MeshWeaver.Test.BootFailedHead";
        await landing.ShelveModule(
                name, [(name + ".dll", ModuleBuiltAgainstThisPlatform(name))], version: "1.7.0")
            .Timeout(TestTimeouts.Convergence).Await();
        var head = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        ModuleActivationSidecar.SetUnloadable(
            root, name, head.Directory!, "boot-measured", "the boot failed to load it");
        Assert.NotNull(Assert.Single(ModuleActivationSidecar.Read(root).Entries).UnloadableFrameworkMvid);

        var outcome = await landing.ShelveModule(
                name, [(name + ".dll", ModuleBuiltAgainstThisPlatform(name))], version: "1.6.1")
            .Timeout(TestTimeouts.Convergence).Await();

        Assert.True(outcome.ShelfOnly);
        Assert.True(outcome.RetainedAsFallback);
        Assert.True(outcome.RestartRequired,
            "the boot already failed to load the 1.7.0 head, so this process runs its fallback — and "
            + "the fallback just moved to 1.6.1, which the next restart loads");
    }

    /// <summary>
    /// 🚨 <b>The third state, and it fails CLOSED.</b> Bytes that are not a readable managed
    /// assembly answer <see cref="ModuleLinkState.Indeterminate"/> — "I could not determine
    /// whether this loads" — and that is NEVER folded into "it loads". A gate whose unknown reads
    /// as a pass is a gate that cannot fail.
    /// </summary>
    [Fact]
    public async Task UnreadableBytes_AreRefused_NotWavedThroughAsUnknown()
    {
        var verdict = ModulePlatformLink.Check(
            [0x4D, 0x5A, 0x90], "MeshWeaver.Test.Garbage",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MeshWeaver.Test.Garbage" },
            ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory));

        Assert.Equal(ModuleLinkState.Indeterminate, verdict.State);
        Assert.False(verdict.MayLoad);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await landing.LandModule(
                    "MeshWeaver.Test.Garbage", [("MeshWeaver.Test.Garbage.dll", [0x4D, 0x5A, 0x90])])
                .Timeout(TestTimeouts.Convergence).Await());
        Assert.Contains("could NOT be determined", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 <b>A module referencing a SIBLING MODULE is not the defect, and must not be refused as
    /// one.</b> A wave lands its modules one at a time, and the sibling this one needs may have
    /// landed thirty seconds ago and not be loaded here at all (restart-as-activation). Measuring
    /// against the application closure alone would find no <c>MeshWeaver.Test.SiblingLib</c>
    /// anywhere, hit the platform-prefix rule, and refuse a module that is perfectly fine — a
    /// false refusal, which on a real deployment means a feature silently missing after an
    /// upgrade.
    ///
    /// <para>The surface therefore carries the ACTIVE generation of every landed module, rebuilt
    /// per landing. Pinning the sibling's landing FIRST is what makes this able to fail: measure
    /// against <c>/app</c> only, and the second landing throws.</para>
    /// </summary>
    [Fact]
    public async Task AModuleReferencingASiblingModuleLandedMomentsEarlier_IsNotRefused()
    {
        var sibling = Emit("MeshWeaver.Test.SiblingLib", """
            namespace MeshWeaver.Test;
            public static class SiblingApi { public static int Answer => 42; }
            """);
        await landing.LandModule(
                "MeshWeaver.Test.SiblingLib", [("MeshWeaver.Test.SiblingLib.dll", sibling)])
            .Timeout(TestTimeouts.Convergence).Await();

        // Compiled against the sibling — and the sibling is NOT in the application closure and is
        // NOT loaded in this process. Only the landed generation can answer for it.
        var dependent = Emit("MeshWeaver.Test.DependentPack", """
            using MeshWeaver.Test;
            public static class DependentViews
            {
                public static int Show() => SiblingApi.Answer;
            }
            """, extra: MetadataReference.CreateFromImage(sibling));

        await landing.LandModule(
                "MeshWeaver.Test.DependentPack", [("MeshWeaver.Test.DependentPack.dll", dependent)])
            .Timeout(TestTimeouts.Convergence).Await();

        var landedNames = ModuleActivationSidecar.Read(root).Entries.Select(e => e.Name).ToArray();
        Assert.Contains("MeshWeaver.Test.SiblingLib", landedNames);
        Assert.Contains("MeshWeaver.Test.DependentPack", landedNames);
    }

    // ───────────────────────────────────────────────────── the probe itself, and its denominator

    /// <summary>
    /// 🚨 <b>The denominator, printed.</b> A clean verdict over ZERO checked references is
    /// indistinguishable from a check that never ran — the sweep-with-no-denominator shape this
    /// codebase keeps meeting. A linkable module must therefore report that it actually resolved
    /// references against real platform assemblies.
    /// </summary>
    [Fact]
    public void ALinkableModule_ReportsANonZeroDenominator()
    {
        var path = Write("MeshWeaver.Test.CountedViewPack",
            ModuleBuiltAgainstThisPlatform("MeshWeaver.Test.CountedViewPack"));

        var verdict = ModulePlatformLink.Check(
            path, ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory));

        Assert.Equal(ModuleLinkState.Linkable, verdict.State);
        Assert.True(verdict.CheckedTypeReferences > 0,
            "a Linkable verdict over zero checked references is a check that never ran: "
            + verdict.Report());
        Assert.Contains(ContractAssembly, verdict.CheckedAssemblies);
    }

    /// <summary>
    /// The coarse-grained half of the same defect: a module referencing a whole PLATFORM assembly
    /// this deployment does not carry. Refused, because that is a certain load failure — while a
    /// non-platform assembly it does not carry is a private dependency, NAMED as unchecked rather
    /// than guessed about.
    /// </summary>
    [Fact]
    public void AWholePlatformAssemblyThisDeploymentLacks_IsRefused()
    {
        var path = Write("MeshWeaver.Test.OrphanViewPack",
            ModuleBuiltAgainstAFuturePlatform("MeshWeaver.Test.OrphanViewPack"));

        // A surface with NO probe directory and NO loaded assemblies carries nothing at all.
        var verdict = ModulePlatformLink.Check(
            path, ModulePlatformSurface.Of(Array.Empty<System.Reflection.Assembly>()));

        Assert.Equal(ModuleLinkState.Unlinkable, verdict.State);
        Assert.Contains(verdict.MissingTypes,
            m => m.Contains("no such platform assembly", StringComparison.Ordinal));
        // System.* is not the platform's, so it is reported as UNCHECKED rather than missing.
        Assert.Contains(verdict.UncheckedAssemblies,
            a => a.StartsWith("System.", StringComparison.Ordinal));
    }

    // ───────────────────────────────────────────────────── the generation is parked, not the portal

    /// <summary>
    /// 🚨 <b>THE repro of #3518's blast radius.</b> An unloadable module costs THAT module and
    /// nothing else: it is never loaded, it is recorded as
    /// <see cref="IncompatibleModule"/> naming the type it wanted, the module beside it installs
    /// normally, and <c>InstallAssemblies</c> does not throw. That is the difference between "one
    /// view pack is absent" and "every render on the portal throws".
    /// </summary>
    [Fact]
    public void AnUnloadableModule_IsParked_AndTheOthersStillInstall()
    {
        var bad = Write("MeshWeaver.Test.ParkedViewPack",
            ModuleBuiltAgainstAFuturePlatform("MeshWeaver.Test.ParkedViewPack"));
        // A real, loadable assembly standing in for the module that must keep working.
        var goodName = typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.GetName().Name!;
        var good = Write(goodName,
            File.ReadAllBytes(typeof(MeshWeaver.Plugin.Packaging.BundleReader).Assembly.Location));

        var services = new ServiceCollection();
        var builder = new MeshBuilder(configure => configure(services), new Address("mesh", "test"));

        // Does not throw: a module that cannot load is a reason to lose THAT module.
        builder.InstallAssemblies(bad, good);

        var provider = services.BuildServiceProvider();
        var parked = Assert.Single(provider.GetServices<IncompatibleModule>());
        Assert.Equal("MeshWeaver.Test.ParkedViewPack", parked.Name);
        Assert.Contains(FutureType, parked.Report(), StringComparison.Ordinal);
        Assert.Contains("CONTRIBUTING NOTHING", parked.Report(), StringComparison.Ordinal);

        Assert.Contains(provider.GetServices<InstalledModuleAssembly>(),
            m => string.Equals(m.Assembly.GetName().Name, goodName, StringComparison.Ordinal));
    }

    /// <summary>
    /// A parked module reads as QUARANTINED on every surface, never as PENDING. "Restart required"
    /// would be a promise no restart can keep — a restart re-runs the same measurement on the same
    /// bytes and refuses again — which is the same false-prompt rule a held entry and a missing
    /// DLL already follow.
    /// </summary>
    [Fact]
    public async Task AParkedModule_ReadsAsQuarantined_NeverAsRestartRequired()
    {
        await landing.LandModule(
                "MeshWeaver.Test.CurrentViewPack",
                [("MeshWeaver.Test.CurrentViewPack.dll",
                    ModuleBuiltAgainstThisPlatform("MeshWeaver.Test.CurrentViewPack"))])
            .Timeout(TestTimeouts.Convergence).Await();

        var report = new PendingModuleActivations(root)
        {
            QuarantinedModules = ImmutableHashSet.Create(
                StringComparer.OrdinalIgnoreCase, "MeshWeaver.Test.CurrentViewPack"),
        }.Read(
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            loadedModuleGenerations: ImmutableDictionary<string, string>.Empty);

        Assert.False(report.HasPending);
        Assert.True(report.HasQuarantined);
        Assert.Equal("MeshWeaver.Test.CurrentViewPack", Assert.Single(report.Quarantined).Name);
        Assert.Contains("REFUSED against this platform build", report.Describe(),
            StringComparison.Ordinal);
        // A blank package path matches nothing — never a wildcard that would put another
        // package's quarantine on this card.
        Assert.False(report.IsQuarantinedForPackage(null));
    }

    /// <summary>
    /// 🚨 A REQUIRED module refused because it needs another platform must NOT stall a rollout.
    /// That is the declared floor's rule, and it exists because no rollout of this deployment can
    /// conjure the platform the module was built for — in the rollback direction, stalling would
    /// hold the very roll that resolves it (the 2026-08-22 three-way deadlock, one lane over).
    ///
    /// <para>An IMAGE-shipped module that cannot link stays <c>Incompatible</c> and DOES stall:
    /// that is a build defect the previous generation does not share.</para>
    /// </summary>
    [Fact]
    public void ARequiredStoreModuleRefusedForItsPlatform_DoesNotStallARollout()
    {
        var refused = IncompatibleModule.FromLinkRefusal(
            "/data/modules/MeshWeaver.Test.ParkedViewPack@1/MeshWeaver.Test.ParkedViewPack.dll",
            ModulePlatformLink.Check(
                ModuleBuiltAgainstAFuturePlatform("MeshWeaver.Test.ParkedViewPack"),
                "MeshWeaver.Test.ParkedViewPack",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "MeshWeaver.Test.ParkedViewPack" },
                ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory)));

        Assert.True(refused.RefusedBeforeLoad);

        // 🚨 The entries carry '.dll'. `Classify` derives the module name with
        // Path.GetFileNameWithoutExtension, which on a suffix-less DOTTED assembly name strips the
        // last segment ("MeshWeaver.Test.ParkedViewPack" → "MeshWeaver.Test") — so a suffix-less
        // entry matches no module and every verdict here would be ExpectedLater by accident. The
        // Name assertions below are what make that failure visible instead of green.
        var storeDelivered = RequiredModuleStatus.Classify(
            requiredEntries: ["MeshWeaver.Test.ParkedViewPack.dll"],
            baselineEntries: [],
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvesFromDeployment: _ => false,
            activation: null,
            landedDllExists: _ => false,
            platformGate: _ => null,
            incompatibleModules: [refused]);
        Assert.Empty(RequiredModuleStatus.Incompatible(storeDelivered));
        var expected = Assert.Single(RequiredModuleStatus.ExpectedLater(storeDelivered));
        Assert.Equal("MeshWeaver.Test.ParkedViewPack", expected.Name);
        // And it says WHY — the link refusal, not a generic "not installed".
        Assert.Contains(FutureType, expected.Reason, StringComparison.Ordinal);

        // The image's OWN pack, unlinkable against the image it shipped in, still stalls.
        var imageShipped = RequiredModuleStatus.Classify(
            requiredEntries: ["MeshWeaver.Test.ParkedViewPack.dll"],
            baselineEntries: ["MeshWeaver.Test.ParkedViewPack.dll"],
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvesFromDeployment: _ => false,
            activation: null,
            landedDllExists: _ => false,
            platformGate: _ => null,
            incompatibleModules: [refused]);
        var stalls = Assert.Single(RequiredModuleStatus.Incompatible(imageShipped));
        Assert.Equal("MeshWeaver.Test.ParkedViewPack", stalls.Name);
    }

    /// <summary>
    /// 🚨 #3649 — a REQUIRED module running its PREVIOUS generation is <c>Present</c>. The loader
    /// registers a <see cref="FallbackModule"/> for it, never an <see cref="IncompatibleModule"/>:
    /// its assembly is loaded and its features work, so the readiness probe stays Healthy and a
    /// rollout is not stalled on a module that is merely one version behind.
    /// </summary>
    [Fact]
    public void ARequiredModuleRunningItsPreviousGeneration_IsPresent_NeverIncompatible()
    {
        var fallback = new FallbackModule(
            "MeshWeaver.Test.FallbackPack",
            "/data/modules/MeshWeaver.Test.FallbackPack@b/MeshWeaver.Test.FallbackPack.dll",
            "/data/modules/MeshWeaver.Test.FallbackPack@a/MeshWeaver.Test.FallbackPack.dll",
            "it references " + FutureType)
        {
            Version = "1.3.0",
            PreviousVersion = "1.2.3",
        };
        Assert.Equal("MeshWeaver.Test.FallbackPack@b", fallback.Generation);
        Assert.Equal("MeshWeaver.Test.FallbackPack@a", fallback.PreviousGeneration);
        Assert.StartsWith(
            "runs v1.2.3 (MeshWeaver.Test.FallbackPack@a); v1.3.0 (MeshWeaver.Test.FallbackPack@b) "
            + "landed but does not load here:", fallback.Describe(), StringComparison.Ordinal);

        // What the loader hands the probe for such a module: the name IS loaded, and the
        // incompatible set does NOT carry it.
        var verdicts = RequiredModuleStatus.Classify(
            requiredEntries: ["MeshWeaver.Test.FallbackPack.dll"],
            baselineEntries: [],
            loadedAssemblyNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "MeshWeaver.Test.FallbackPack" },
            resolvesFromDeployment: _ => false,
            activation: null,
            landedDllExists: _ => true,
            platformGate: _ => null,
            incompatibleModules: []);

        var verdict = Assert.Single(verdicts);
        Assert.Equal("MeshWeaver.Test.FallbackPack", verdict.Name);
        Assert.Equal(RequiredModuleState.Present, verdict.State);
        Assert.Empty(RequiredModuleStatus.Incompatible(verdicts));
        Assert.Empty(RequiredModuleStatus.ExpectedLater(verdicts));
    }

    // ───────────────────────────────────────────────────────────── harness

    /// <summary>Different assembly versions and additive APIs do not remove a referenced type.
    /// Exercise the real metadata probe with separately compiled contracts in both directions.</summary>
    [Theory]
    [InlineData("1.0.0.0", "9.0.0.0")]
    [InlineData("9.0.0.0", "1.0.0.0")]
    public void CompatibleApiAcrossPlatformVersions_IsLinkable(string builtVersion, string runningVersion)
    {
        const string contractName = "MeshWeaver.Test.VersionedContract";
        const string moduleName = "MeshWeaver.Test.VersionTolerantPack";
        var builtContract = Emit(contractName, $$"""
            [assembly: System.Reflection.AssemblyVersion("{{builtVersion}}")]
            namespace MeshWeaver.Test;
            public class StableApi { public int Answer() => 1; }
            """);
        var module = Emit(moduleName, """
            public class View {
                public int Render(MeshWeaver.Test.StableApi api) => api.Answer();
            }
            """, extra: MetadataReference.CreateFromImage(builtContract));
        var runningContract = Emit(contractName, $$"""
            [assembly: System.Reflection.AssemblyVersion("{{runningVersion}}")]
            namespace MeshWeaver.Test;
            public class StableApi { public int Answer() => 42; public string Extra() => "new"; }
            public class UnusedAddition { }
            """);

        var surface = ModulePlatformSurface.OfFiles([Write(contractName, runningContract)]);
        var verdict = ModulePlatformLink.Check(module, moduleName, new HashSet<string> { moduleName }, surface);

        Assert.Equal(ModuleLinkState.Linkable, verdict.State);
        Assert.True(verdict.MayLoad, verdict.Report());
        Assert.True(verdict.CheckedTypeReferences > 0);
        Assert.Contains(contractName, verdict.CheckedAssemblies);
        Assert.Empty(verdict.MissingTypes);
    }

    /// <summary>The same version can contain incompatible bytes. Version equality must never
    /// replace the type measurement, even when an unrelated type remains in the assembly.</summary>
    [Fact]
    public void IdenticalPlatformVersionWithRemovedApi_IsUnlinkable()
    {
        const string contractName = "MeshWeaver.Test.SameVersionContract";
        const string moduleName = "MeshWeaver.Test.RemovedApiPack";
        var builtContract = Emit(contractName, """
            [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
            namespace MeshWeaver.Test;
            public class RequiredApi { }
            """);
        var module = Emit(moduleName, """
            public class View { public MeshWeaver.Test.RequiredApi Render() => new(); }
            """, extra: MetadataReference.CreateFromImage(builtContract));
        var runningContract = Emit(contractName, """
            [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
            namespace MeshWeaver.Test;
            public class UnrelatedApi { }
            """);

        var verdict = ModulePlatformLink.Check(module, moduleName, new HashSet<string> { moduleName },
            ModulePlatformSurface.OfFiles([Write(contractName, runningContract)]));

        Assert.Equal(ModuleLinkState.Unlinkable, verdict.State);
        Assert.False(verdict.MayLoad);
        Assert.Contains(verdict.MissingTypes, missing => missing.Contains("MeshWeaver.Test.RequiredApi", StringComparison.Ordinal));
        Assert.Contains(contractName, verdict.Report(), StringComparison.Ordinal);
    }

    /// <summary>Exercise real method binding as well as type metadata. A changed method body or
    /// added overload keeps the used signature working; removing/changing it fails when invoked.
    /// The type probe alone deliberately makes no member-compatibility claim.</summary>
    [Theory]
    [InlineData("public int Answer() => 42; public int Answer(int extra) => extra;", true)]
    [InlineData("public long Answer() => 42;", false)]
    [InlineData("public int Answer(int extra) => extra;", false)]
    public void MemberCompatibility_IsMeasuredByBindingTheUsedSignature(string runtimeMember, bool compatible)
    {
        const string contractName = "MeshWeaver.Test.MemberContract";
        const string moduleName = "MeshWeaver.Test.MemberConsumer";
        var builtContract = Emit(contractName,
            "namespace MeshWeaver.Test; public class Api { public int Answer() => 1; }");
        var consumer = Emit(moduleName,
            "public static class View { public static int Render() => new MeshWeaver.Test.Api().Answer(); }",
            extra: MetadataReference.CreateFromImage(builtContract));
        var runtimeContract = Emit(contractName,
            "namespace MeshWeaver.Test; public class Api { " + runtimeMember + " }");

        var typeVerdict = ModulePlatformLink.Check(consumer, moduleName, new HashSet<string> { moduleName },
            ModulePlatformSurface.OfFiles([Write(contractName, runtimeContract)]));
        Assert.Equal(ModuleLinkState.Linkable, typeVerdict.State);

        var context = new AssemblyLoadContext("member-contract-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var runtime = new MemoryStream(runtimeContract);
            context.LoadFromStream(runtime);
            using var module = new MemoryStream(consumer);
            var render = context.LoadFromStream(module).GetType("View")!.GetMethod("Render")!;
            if (compatible)
                Assert.Equal(42, render.Invoke(null, null));
            else
            {
                var error = Assert.Throws<TargetInvocationException>(() => render.Invoke(null, null));
                var missing = Assert.IsType<MissingMethodException>(error.InnerException);
                Assert.Contains("Answer", missing.Message, StringComparison.Ordinal);
            }
        }
        finally { context.Unload(); }
    }

    /// <summary>An incompatible incoming generation must not replace the working activation.
    /// Run both real landings and verify the previous bytes and activation pointer survive.</summary>
    [Fact]
    public async Task MissingApiInUpgrade_PreservesWorkingModuleAndItsBytes()
    {
        const string name = "MeshWeaver.Test.ContinuityPack";
        var good = ModuleBuiltAgainstThisPlatform(name);
        await landing.LandModule(name, [(name + ".dll", good)], version: "1.0.0")
            .Timeout(TestTimeouts.Convergence).Await();
        var before = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        var goodPath = Path.Combine(ModuleLandingService.ModuleDirectoryFor(root, name, before), name + ".dll");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await landing.LandModule(name, [(name + ".dll", ModuleBuiltAgainstAFuturePlatform(name))],
                    version: "2.0.0", minMeshVersion: "0.0.1")
                .Timeout(TestTimeouts.Convergence).Await());

        Assert.Contains(FutureType, refusal.Message, StringComparison.Ordinal);
        var after = Assert.Single(ModuleActivationSidecar.Read(root).Entries);
        Assert.Equal(before.Directory, after.Directory);
        Assert.Equal(before.Version, after.Version);
        Assert.True(after.Enabled);
        Assert.Equal(good, File.ReadAllBytes(goodPath));
    }

    /// <summary>
    /// A module compiled against a STAND-IN <c>MeshWeaver.Mesh.Contract</c> that carries
    /// <see cref="FutureType"/> — the assembly a platform three days ahead would have shipped. The
    /// bytes therefore reference a type by the same assembly simple name this process has loaded,
    /// and that copy does not have it: memex-cloud's exact shape, and one no version comparison
    /// can see.
    /// </summary>
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

    /// <summary>A module compiled against the REAL contract this process is running, using a type
    /// it genuinely has — the control that proves the gate does not simply refuse everything.</summary>
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
    /// <param name="assemblyName">The emitted assembly's simple name.</param>
    /// <param name="source">The C# to compile.</param>
    /// <param name="excludeReference">A reference assembly to LEAVE OUT — how a stand-in of the
    /// same simple name is substituted for the platform's own.</param>
    /// <param name="extra">References to add, e.g. the stand-in.</param>
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

    /// <summary>Writes an assembly into its OWN directory under the test root, so its "siblings"
    /// are exactly its own closure — the shape a landed generation directory has.</summary>
    private string Write(string assemblyName, byte[] bytes)
    {
        var directory = Path.Combine(root, "emitted", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, assemblyName + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
