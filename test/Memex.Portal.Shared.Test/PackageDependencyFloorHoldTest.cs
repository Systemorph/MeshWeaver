#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>MeshWeaver#6192, reproduced on the lane that caused it</b> — the unattended registry update.
///
/// <para>Measured on the control instance on 2026-10-10/11: Governance auto-updated to a version
/// declaring <c>Essentials@^1.12.0</c> while the Essentials module that ran was 1.11.26, and
/// AppleWeather to one declaring <c>AI@^1.27.0</c> beside AI 1.26.2. The install records then stated
/// a floor the running dependency did not meet, so every module-set proposal on the deployment was
/// refused (<c>[ModuleSet] NOT proposing the landed module set</c>, logged on every reconcile of
/// every replica). Neither package ships a module, so nothing on the module lane could withhold
/// them: the update itself has to be held.</para>
///
/// <para>The fixture is the platform-floor one (<see cref="FloorHoldFixture"/>): a package installed
/// at v1 whose v2 declares a requirement, driven through the real
/// <see cref="PackageUpdateReconciler"/>, with the source's fetch log as the witness that the held
/// version never travelled. What this process "has loaded" is the <see cref="ILoadedPackageModules"/>
/// reading, set by the test because a test process cannot load two builds of one module.</para>
/// </summary>
public class PackageDependencyFloorHoldTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Dependency = "DepPkg";
    private const string DependencyModule = "MeshWeaver.DepPkg";
    private const string Requirement = Dependency + "@^1.12.0";

    private readonly SettableLoadedModules loaded = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                // Registered AFTER the catalog's own reader, so this reading is the one resolved.
                .AddSingleton<ILoadedPackageModules>(loaded));

    private ILogger Logger => Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<PackageDependencyFloorHoldTest>();

    private static PackageManifest V2Requiring(string requirement) =>
        FloorHoldFixture.V2(floor: null!) with { Requires = [requirement] };

    private async Task InstallV1(PackageUpdatePolicy policy)
    {
        var ct = TestContext.Current.CancellationToken;
        await Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>().Completed
            .FirstAsync().Timeout(180.Seconds()).Await(ct);
        Mesh.ServiceProvider.GetRequiredService<ILoadedPackageModules>().Should().BeSameAs(loaded,
            "the precondition of every arm: the gate reads the reading this test sets");
        await FloorHoldFixture.InstallV1(Mesh, Logger, ct);
        await PackageInstaller.SetUpdatePolicy(Mesh, FloorHoldFixture.Package, policy, Logger)
            .Timeout(60.Seconds()).Await(ct);
    }

    /// <summary>
    /// THE reproduction: the loaded dependency is below the candidate's floor → the update is held,
    /// nothing is fetched, the installed sources and the record's requirements stay → ticked again,
    /// one held state → a satisfying dependency is loaded → the same update lands and the hold clears.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnAutoUpdate_IsHeldWhileTheLoadedDependencyIsBelowItsFloor_ThenLands()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallV1(PackageUpdatePolicy.Auto);
        loaded.Set(Dependency, DependencyModule, "1.11.26");

        var source = new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files());
        await FloorHoldFixture.Reconcile(Mesh, source, V2Requiring(Requirement), Logger, ct);

        var held = await FloorHoldFixture.AwaitRecord(Mesh, r => r.HeldUpdate is not null, ct);
        source.Fetches.Should().Be(0, "the held version's sources are never fetched, so nothing can compile them");
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V1Text, "the installed version keeps serving");
        held.ModuleVersion.Should().Be(FloorHoldFixture.V1Hash, "the record still describes the installed version");
        held.Requires.Should().BeEmpty(
            "THE property the module set depends on: the record does not take the floor the dependency does not meet");
        held.HeldUpdate.Should().Contain("held").And.Contain(FloorHoldFixture.V2Version).And.Contain(Requirement)
            .And.Contain(DependencyModule).And.Contain("1.11.26");
        PackageDependencyFloorGate.IsDependencyHold(held.HeldUpdate).Should().BeTrue();
        PackagePlatformFloorGate.IsFloorHold(held.HeldUpdate).Should().BeFalse(
            "a met platform floor must not clear a dependency hold");
        held.HeldSince.Should().NotBeNull("the fleet view's 'held since' reads it");

        // ── the same held state, ticked again: nothing is fetched and the since-when stays ──────
        await FloorHoldFixture.Reconcile(Mesh, source, V2Requiring(Requirement), Logger, ct);
        await FloorHoldFixture.Reconcile(Mesh, source, V2Requiring(Requirement), Logger, ct);
        source.Fetches.Should().Be(0);
        var again = await FloorHoldFixture.AwaitRecord(Mesh, r => r.HeldUpdate is not null, ct);
        again.HeldUpdate.Should().Be(held.HeldUpdate);
        again.HeldSince.Should().Be(held.HeldSince, "an unchanged hold keeps the moment it began");
        again.ModuleVersion.Should().Be(FloorHoldFixture.V1Hash);

        // ── a satisfying dependency is loaded: the same update lands and the hold clears ────────
        loaded.Set(Dependency, DependencyModule, "1.12.0");
        await FloorHoldFixture.Reconcile(Mesh, source, V2Requiring(Requirement), Logger, ct);
        var landed = await FloorHoldFixture.AwaitRecord(Mesh, r => r.ModuleVersion == FloorHoldFixture.V2Hash, ct);
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V2Text,
            "the positive control: with the requirement met, the same update lands");
        source.Fetches.Should().BeGreaterThan(0);
        landed.HeldUpdate.Should().BeNull("an install of the candidate ends the hold");
        landed.HeldSince.Should().BeNull();
    }

    /// <summary>
    /// The negative control of the hold, and the rule's other half: a dependency this process has
    /// NOT loaded a module for is not judged, so the update lands as it did before the gate existed.
    /// A gate that held everything fails here.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADependencyWithNoLoadedModule_IsNotJudged_AndTheUpdateLands()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallV1(PackageUpdatePolicy.Auto);
        loaded.Set("SomeOtherPackage", "MeshWeaver.Other", "0.0.1");

        var source = new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files());
        await FloorHoldFixture.Reconcile(Mesh, source, V2Requiring(Requirement), Logger, ct);

        var landed = await FloorHoldFixture.AwaitRecord(Mesh, r => r.ModuleVersion == FloorHoldFixture.V2Hash, ct);
        landed.HeldUpdate.Should().BeNull();
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V2Text);
    }

    /// <summary>
    /// The reminder lane: an update this instance cannot run is not OFFERED either, and it is offered
    /// once the dependency is loaded.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ANotifyUpdate_IsNotOfferedWhileHeld_AndIsOfferedOnceTheDependencyIsLoaded()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallV1(PackageUpdatePolicy.Notify);
        loaded.Set(Dependency, DependencyModule, "1.11.26");

        var source = new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files());
        await FloorHoldFixture.Reconcile(Mesh, source, V2Requiring(Requirement), Logger, ct);
        var held = await FloorHoldFixture.AwaitRecord(Mesh, r => r.HeldUpdate is not null, ct);
        held.NotifiedModuleVersion.Should().NotBe(FloorHoldFixture.V2Hash, "a held update raises no reminder");
        PackageDependencyFloorGate.IsDependencyHold(held.HeldUpdate).Should().BeTrue();

        loaded.Set(Dependency, DependencyModule, "1.12.4");
        await FloorHoldFixture.Reconcile(Mesh, source, V2Requiring(Requirement), Logger, ct);
        var offered = await FloorHoldFixture.AwaitRecord(
            Mesh, r => r.NotifiedModuleVersion == FloorHoldFixture.V2Hash, ct);
        offered.HeldUpdate.Should().BeNull("the met requirement clears the hold before the reminder is raised");
        offered.ModuleVersion.Should().Be(FloorHoldFixture.V1Hash, "a reminder applies nothing");
        source.Fetches.Should().Be(0);
    }

    /// <summary>
    /// The ONE install orchestrator holds too — the boot install and the click go through it, not
    /// through the reconciler — and the same call lands the update once the dependency is loaded.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task InstallOrUpdate_HoldsTheUpdate_ReturnsNothingWritten_ThenLands()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallV1(PackageUpdatePolicy.Auto);
        loaded.Set(Dependency, DependencyModule, "1.11.26");

        var source = new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files());
        var result = await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, source, "HEAD", V2Requiring(Requirement), Logger)
            .Timeout(120.Seconds()).Await(ct);

        result.Should().Be(new InstallResult(0, 0));
        source.Fetches.Should().Be(0);
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V1Text);
        var held = await FloorHoldFixture.AwaitRecord(Mesh, r => r.HeldUpdate is not null, ct);
        held.ModuleVersion.Should().Be(FloorHoldFixture.V1Hash);
        held.Requires.Should().BeEmpty();
        PackageDependencyFloorGate.IsDependencyHold(held.HeldUpdate).Should().BeTrue();

        loaded.Set(Dependency, DependencyModule, "1.12.0");
        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, source, "HEAD", V2Requiring(Requirement), Logger)
            .Timeout(180.Seconds()).Await(ct);
        var landed = await FloorHoldFixture.AwaitRecord(Mesh, r => r.ModuleVersion == FloorHoldFixture.V2Hash, ct);
        landed.HeldUpdate.Should().BeNull();
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V2Text);
    }

    /// <summary>
    /// A hold is DECIDED on a record read before it is written. When the candidate lands in between
    /// (another replica whose loaded dependency meets the floor, or a click), the late hold must not
    /// be stamped on the installed version: the next reconcile ends at the equal-hash gate, before
    /// anything clears a hold, so it would stay for good.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AHoldDecidedOnAStaleRecord_IsNotStampedOnceTheCandidateHasLanded()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallV1(PackageUpdatePolicy.Auto);
        var staleRecord = await FloorHoldFixture.AwaitRecord(Mesh, r => r.ModuleVersion == FloorHoldFixture.V1Hash, ct);
        var candidate = V2Requiring(Requirement);

        // The candidate lands while the hold is in flight (no loaded reading for the dependency here).
        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files()), "HEAD", candidate, Logger)
            .Timeout(180.Seconds()).Await(ct);
        await FloorHoldFixture.AwaitRecord(Mesh, r => r.ModuleVersion == FloorHoldFixture.V2Hash, ct);

        var unmet = new UnmetPackageDependency(Requirement, Dependency, DependencyModule, "1.11.26");
        await PackageDependencyFloorGate.RecordHold(Mesh, candidate, staleRecord, unmet, Logger)
            .Timeout(60.Seconds()).Await(ct);

        var after = await FloorHoldFixture.AwaitRecord(Mesh, r => r.ModuleVersion == FloorHoldFixture.V2Hash, ct);
        after.HeldUpdate.Should().BeNull("the candidate is what is installed — there is no update left to hold");
        after.HeldSince.Should().BeNull();

        // The control: on a record still at the installed version the same call does stamp the hold.
        PackageDependencyFloorGate.AlreadyAt(staleRecord, candidate).Should().BeFalse();
        PackageDependencyFloorGate.AlreadyAt(after, candidate).Should().BeTrue();
    }

    /// <summary>
    /// A FRESH install is not held — the CURRENT behaviour, stated, not a claim that it is safe: a
    /// dependency installed earlier in the same closure is landed, not loaded. Refusing it on the
    /// loaded reading would fail a new instance's first boot for every default package whose floor is
    /// above the image's own module copy; the module-set proposal still names such a floor.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AFreshInstall_IsNotHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        await Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>().Completed
            .FirstAsync().Timeout(180.Seconds()).Await(ct);
        loaded.Set(Dependency, DependencyModule, "1.11.26");

        var source = new FloorHoldFixture.CountingSource(FloorHoldFixture.V2Files());
        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, source, "HEAD", V2Requiring(Requirement), Logger)
            .Timeout(180.Seconds()).Await(ct);

        var installed = await FloorHoldFixture.AwaitRecord(Mesh, r => r.ModuleVersion == FloorHoldFixture.V2Hash, ct);
        installed.HeldUpdate.Should().BeNull();
        (await FloorHoldFixture.Text(Mesh)).Should().Be(FloorHoldFixture.V2Text);
    }

    /// <summary>The loaded-module reading, set by the test.</summary>
    private sealed class SettableLoadedModules : ILoadedPackageModules
    {
        private readonly BehaviorSubject<ImmutableDictionary<string, LoadedPackageModule>> current =
            new(ImmutableDictionary<string, LoadedPackageModule>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

        public void Set(string package, string module, string version)
            => current.OnNext(current.Value.SetItem(package, new LoadedPackageModule(module, version)));

        public IObservable<ImmutableDictionary<string, LoadedPackageModule>> Read() => current.Take(1);
    }
}

/// <summary>The pure decision, and what it means for the module set (the symptom of MeshWeaver#6192).</summary>
public class PackageDependencyFloorDecisionTest
{
    private static ImmutableDictionary<string, LoadedPackageModule> Loaded(string package, string version) =>
        ImmutableDictionary<string, LoadedPackageModule>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
            .Add(package, new LoadedPackageModule("MeshWeaver.Markdown.Collaboration", version));

    private static PackageManifest Governance(params string[] requires) => new()
    {
        Id = "Governance",
        Version = "0.16.1",
        ReleasedVersion = "0.16.1",
        Requires = [.. requires],
    };

    [Theory]
    [InlineData("Essentials@^1.12.0", "1.11.26", true)]
    [InlineData("Essentials@^1.12.0", "1.12.0", false)]
    [InlineData("Essentials@^1.12.0", "1.13.4", false)]
    [InlineData("Essentials@^1.12.0", "2.0.0", true)]
    [InlineData("essentials@^1.12.0", "1.11.26", true)]
    [InlineData("Essentials", "1.11.26", false)]
    [InlineData("Essentials@not-a-range", "1.11.26", false)]
    [InlineData("AI@^1.27.0", "1.11.26", false)]
    public void ARequirementIsUnmet_OnlyWhenTheLoadedDependencyIsOutsideItsRange(
        string requirement, string loadedEssentials, bool held)
    {
        var unmet = PackageDependencyFloorGate.Unmet(Governance(requirement), Loaded("Essentials", loadedEssentials));

        (unmet is not null).Should().Be(held);
        if (unmet is null)
            return;
        unmet.Requirement.Should().Be(requirement);
        unmet.Module.Should().Be("MeshWeaver.Markdown.Collaboration");
        unmet.LoadedVersion.Should().Be(loadedEssentials);
        var sentence = PackageDependencyFloorGate.HeldSentence(Governance(requirement), unmet);
        sentence.Should().Contain("0.16.1").And.Contain(requirement).And.Contain(loadedEssentials);
        PackageDependencyFloorGate.IsDependencyHold(sentence).Should().BeTrue();
        PackagePlatformFloorGate.IsFloorHold(sentence).Should().BeFalse();
    }

    [Fact]
    public void TheOtherHolds_AreNotDependencyHolds()
    {
        var floor = PackagePlatformFloorGate.HeldSentence(
            new PackageManifest { Id = "Store", Version = "1.16.0", MinMeshVersion = "3.0.0-ci.9494" },
            PlatformFloor.Evaluate("3.0.0-ci.9494", "3.0.0-ci.9412"));
        PackageDependencyFloorGate.IsDependencyHold(floor).Should().BeFalse();
        PackageDependencyFloorGate.IsDependencyHold(null).Should().BeFalse();
        PackageDependencyFloorGate.IsDependencyHold(
            "held: 1.12.5 — 'Essentials' is kept by its sync source, which has landed a, not b; converges when "
            + "the sync lands it (the seal for this platform)").Should().BeFalse();
    }

    /// <summary>
    /// What the hold buys the MODULE SET: with the record kept at the installed version the proposal
    /// has no unmet floor; with the record advanced (the behaviour before the gate) the whole wave is
    /// refused — for a package that lands no module, which no module-lane rule can hold back.
    /// </summary>
    [Fact]
    public void AHeldRecord_LeavesTheModuleSetProposable_WhereAnAdvancedOneRefusesTheWave()
    {
        var essentials = new PackageManifest
        {
            Id = "Essentials",
            Module = "MeshWeaver.Markdown.Collaboration",
            ReleasedVersion = "1.11.26",
        };
        var landed = new ModuleActivationList
        {
            Entries =
            [
                new ModuleActivationEntry
                {
                    Name = "MeshWeaver.Markdown.Collaboration",
                    PackagePath = "Plugins/Essentials",
                    Version = "1.11.26",
                    Directory = "g-essentials-1-11-26",
                    Enabled = true,
                },
            ],
        };
        var installedBefore = Governance("Essentials@^1.11.0") with { Version = "0.15.2", ReleasedVersion = "0.15.2" };
        var advanced = Governance("Essentials@^1.12.0");

        ModuleDependencyFloor.Unmet([essentials, installedBefore], landed).Should().BeEmpty(
            "the held record keeps the requirement the running dependency meets");
        var refused = ModuleDependencyFloor.Unmet([essentials, advanced], landed).Should().ContainSingle().Subject;
        refused.Dependent.Should().Be("Governance");
        refused.LoadedVersion.Should().Be("1.11.26");
    }
}
