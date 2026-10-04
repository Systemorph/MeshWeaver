using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>Held updates CONVERGE (maintainer, 2026-10-04: "we must know which version we want to test
/// and update everyone to latest").</b> Measured on the control instance that night: <c>Plugins/AI</c>
/// stayed at 1.18.1 with <c>"held: 1.19.4 needs platform ≥ 3.0.0-ci.9887 … updates when the platform
/// rolls"</c>, the platform rolled to 9887 and nothing applied it; <c>Plugins/Hosting</c> had been held
/// since 09-18. Both partitions carry a <c>_GitSync</c>, so once the floor was met the unattended lane
/// degraded to the sync-owned REMINDER (One Partition, One Bookkeeping, gates 1 and 1b) — and no lane
/// ever landed the MODULE of a sync-owned package. A human ran RefreshModules each time.
///
/// <para>The rule pinned here: when the partition's sync source has landed EXACTLY the candidate's
/// content, the install record adopts it without writing a node (still one writer), and the module
/// lane is licensed to land the matching code. When the sync has landed something else, the hold is
/// stamped on the record WITH its reason and since-when, so the fleet view lists it.</para>
/// </summary>
public class HeldUpdatesConvergeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .AddPluginCatalog()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton(new PluginCatalogOptions { AutoUpdateByDefault = true });
                return services;
            });

    private const string V1 = "1111111111111111";
    private const string V2 = "2222222222222222";
    private const string Converges = "ConvergesPkg";
    private const string StaysHeld = "StaysHeldPkg";

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private ILogger Logger => Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
        .CreateLogger<HeldUpdatesConvergeTest>();

    /// <summary>
    /// THE 10-04 shape, both arms: two sync-owned packages installed at V1, the registry serves V2 of
    /// both; one partition's sync has LANDED V2, the other's still carries V1.
    ///
    /// <para><b>Fails on main.</b> There a sync-owned partition always degrades to the reminder, so
    /// <c>Converges</c>' record stays at V1 for ever — the AI and Hosting holds verbatim.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ASyncOwnedPackage_ConvergesOnceItsSyncHasLandedTheCandidate_AndOtherwiseSaysWhyItIsHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new RecordingSource(new Dictionary<string, IReadOnlyList<PackageFile>>
        {
            [Converges] = Files(Converges, V1),
            [StaysHeld] = Files(StaysHeld, V1),
        });
        await Install(first, Converges, V1, ct);
        await Install(first, StaysHeld, V1, ct);

        // The one fact that differs: what each partition's sync source has LANDED.
        await TrackPartition(Converges, landed: V2, ct);
        await TrackPartition(StaysHeld, landed: V1, ct);

        var second = new RecordingSource(new Dictionary<string, IReadOnlyList<PackageFile>>
        {
            [Converges] = Files(Converges, V2),
            [StaysHeld] = Files(StaysHeld, V2),
        });
        await PackageUpdateReconciler.ReconcileInstalled(
                Mesh, second, "HEAD", [Candidate(Converges, V2), Candidate(StaysHeld, V2)],
                "Served by registry 'test'", Logger)
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit("the reconcile pass must complete before anything about it is asserted", cancellationToken: ct);

        second.Fetched.Should().BeEmpty(
            "neither partition is the installer's to write — converging must not fetch, let alone "
            + "write, a single file (still ONE writer per partition, MeshWeaver#4355)");

        var converged = await AwaitRecord(Converges, r => r.ModuleVersion == V2, ct);
        converged.HeldUpdate.Should().BeNull("a converged package carries no hold");
        converged.HeldSince.Should().BeNull();
        converged.NotifiedModuleVersion.Should().Be(V2, "no reminder is owed for a version that is installed");

        var held = await AwaitRecord(StaysHeld, r => r.HeldUpdate is not null, ct);
        held.ModuleVersion.Should().Be(V1, "a sync that has not landed the candidate keeps the hold");
        held.HeldUpdate.Should().Contain("kept by its sync source").And.Contain(V1).And.Contain(V2,
            "the record — and the fleet view that lists it — says WHY it is held and what it waits for");
        held.HeldSince.Should().NotBeNull("the view's 'held since' and the too-long rule read it");

        // ── The same hold, decided again: its since-when must SURVIVE (review on #6065 — clearing a
        //    non-floor hold on every pass reset it, so the too-long rule could never fire). ────────
        var since = held.HeldSince;
        await PackageUpdateReconciler.ReconcileInstalled(
                Mesh, second, "HEAD", [Candidate(StaysHeld, V2)], "Served by registry 'test'", Logger)
            .Should().Within(TestTimeouts.CrossSilo).Emit(cancellationToken: ct);
        (await AwaitRecord(StaysHeld, r => r.HeldUpdate is not null, ct)).HeldSince.Should().Be(since,
            "a hold that is still in force keeps the instant it began");

        // ── The sync lands V2 for the held one too: the next pass converges it. ─────────────────
        await TrackPartition(StaysHeld, landed: V2, ct);
        await PackageUpdateReconciler.ReconcileInstalled(
                Mesh, second, "HEAD", [Candidate(StaysHeld, V2)], "Served by registry 'test'", Logger)
            .Should().Within(TestTimeouts.CrossSilo).Emit(cancellationToken: ct);
        var later = await AwaitRecord(StaysHeld, r => r.ModuleVersion == V2, ct);
        later.HeldUpdate.Should().BeNull("the hold clears the moment its condition is true");
        later.HeldSince.Should().BeNull();
        second.Fetched.Should().BeEmpty();
    }

    private static PackageManifest Candidate(string id, string moduleVersion) => new()
    {
        Id = id,
        Name = id,
        Kind = PackageKind.NodeRepo,
        TargetPartition = id,
        SourceFolder = id,
        Version = moduleVersion == V1 ? "1.0.0" : "1.1.0",
        ModuleVersion = moduleVersion,
    };

    private static IReadOnlyList<PackageFile> Files(string id, string moduleVersion) =>
    [
        new PackageFile($"{id}/{ModuleManifest.FileName}", $$"""
            {
              "module": "{{id}}",
              "moduleVersion": "{{moduleVersion}}",
              "version": "1.0.0",
              "files": {
                "{{id}}/index.json": "aaa",
                "{{id}}/Guide.md": "{{moduleVersion}}"
              }
            }
            """),
        new PackageFile($"{id}/index.json", $$"""
            {
              "id": "{{id}}",
              "path": "{{id}}",
              "nodeType": "Space",
              "name": "{{id}}",
              "state": "Active"
            }
            """),
        new PackageFile($"{id}/Guide.md", $"# Guide\n\nGeneration {moduleVersion}."),
    ];

    private async Task Install(IPackageSource source, string id, string moduleVersion, CancellationToken ct)
    {
        await CatalogLayoutAreas.InstallOrUpdate(Mesh, source, "HEAD", Candidate(id, moduleVersion), Logger)
            .Should().Within(TestTimeouts.CrossSilo)
            .Emit($"installing {id} at {moduleVersion} is a precondition", cancellationToken: ct);
        (await AwaitRecord(id, r => r.ModuleVersion == moduleVersion, ct)).EffectiveUpdatePolicy
            .Should().Be(PackageUpdatePolicy.Auto, "the premise is an UNATTENDED apply");
    }

    /// <summary>A real <c>{partition}/_GitSync</c> naming a repository, with the module version the
    /// sync last LANDED — read back through the seam under test before the test proceeds.</summary>
    private async Task TrackPartition(string partition, string landed, CancellationToken ct)
    {
        var config = new GitHubSyncConfig
        {
            RepositoryUrl = "https://github.com/Systemorph/Example",
            Branch = "main",
            ModuleVersions = ImmutableDictionary<string, string>.Empty.Add(partition, landed),
        };
        var path = $"{partition}/{GitHubSyncService.ConfigId}";
        var exists = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        if (exists is null)
            await MeshService.CreateNode(new MeshNode(GitHubSyncService.ConfigId, partition)
                {
                    Name = "GitHub Sync",
                    NodeType = GitHubSyncService.ConfigNodeType,
                    State = MeshNodeState.Active,
                    Content = config,
                })
                .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: ct);
        else
            await Mesh.GetMeshNodeStream(path)
                .Update<GitHubSyncConfig>(_ => config)
                .Should().Within(TestTimeouts.WriteConvergence).Emit(cancellationToken: ct);

        var reading = await Observable.Interval(TestTimeouts.Quick / 20).StartWith(0L)
            .SelectMany(_ => PartitionContentOwnership.SyncedModules(Mesh, partition))
            .Where(r => r.Delivered(partition, landed))
            .FirstAsync()
            .Timeout(TestTimeouts.CrossSilo)
            .Await(ct);
        reading.Known.Should().BeTrue("the seam must read the sync's landed version before the arm means anything");
    }

    private Task<PackageManifest> AwaitRecord(string id, Func<PackageManifest, bool> predicate, CancellationToken ct) =>
        Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .SelectMany(_ => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
                .Read($"{PackageInstaller.InstalledPartition}/{id}", Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null))
            .Select(n => n?.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions))
            .OfType<PackageManifest>()
            .Where(predicate)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(120))
            .Await(ct);

    private sealed record Fetch(string PackageId, IReadOnlyCollection<string>? Paths);

    private sealed class RecordingSource(IReadOnlyDictionary<string, IReadOnlyList<PackageFile>> byPackage)
        : IPackageSource
    {
        private ImmutableList<Fetch> fetched = ImmutableList<Fetch>.Empty;

        public ImmutableList<Fetch> Fetched => fetched;

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>(
                byPackage.Keys.Select(id => Candidate(id, V1)).ToList());

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            FetchPackageFiles(package, gitRef, null);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef, IReadOnlyCollection<string>? paths) =>
            Observable.Defer(() =>
            {
                ImmutableInterlocked.Update(ref fetched, f => f.Add(new Fetch(package.Id, paths)));
                var files = byPackage[package.Id];
                return Observable.Return<IReadOnlyList<PackageFile>>(paths is null
                    ? files
                    : files.Where(f => f.RelativePath.EndsWith(ModuleManifest.FileName, StringComparison.Ordinal)
                                       || paths.Contains(f.RelativePath)).ToList());
            });
    }
}

/// <summary>The convergence rules, pure — every arm, no mesh.</summary>
public class HeldUpdateConvergenceRulesTest
{
    private static readonly PartitionContentOwnershipVerdict SyncOwned =
        new(PartitionContentOwner.SyncSource, "P", "synced");
    private static readonly PartitionContentOwnershipVerdict InstallerOwned =
        new(PartitionContentOwner.Installer, "P", "installer");
    private static readonly PartitionContentOwnershipVerdict Undetermined =
        new(PartitionContentOwner.Undetermined, "P", "unknown");

    [Fact]
    public void Delivered_IsTrueOnlyForAKnownReading_ThatNamesExactlyTheCandidate()
    {
        var landed = SyncedModuleVersions.Of([new("AI", "abc")]);
        landed.Delivered("AI", "abc").Should().BeTrue();
        landed.Delivered("ai", "abc").Should().BeTrue("module ids are case-insensitive, hashes are not");
        landed.Delivered("AI", "ABC").Should().BeFalse();
        landed.Delivered("AI", "other").Should().BeFalse("the sync landed a different tree");
        landed.Delivered("Hosting", "abc").Should().BeFalse("the sync reports nothing for that module");
        landed.Delivered("AI", null).Should().BeFalse("a blank candidate is no evidence");
        SyncedModuleVersions.Unknown.Delivered("AI", "abc").Should().BeFalse("cannot-tell licenses nothing");
    }

    [Fact]
    public void FoldSynced_IsKnownWhenAnyProviderAnswers_AndUnknownOtherwise()
    {
        PartitionContentOwnership.FoldSynced([SyncedModuleVersions.Unknown]).Known.Should().BeFalse();
        PartitionContentOwnership.FoldSynced([]).Known.Should().BeFalse();
        var folded = PartitionContentOwnership.FoldSynced(
            [SyncedModuleVersions.Unknown, SyncedModuleVersions.Of([new("AI", "abc")])]);
        folded.Known.Should().BeTrue();
        folded.Delivered("AI", "abc").Should().BeTrue();
    }

    [Fact]
    public void SyncDelivered_ConvergesOnlyASyncOwnedPartition()
    {
        var landed = SyncedModuleVersions.Of([new("P", "abc")]);
        PartitionContentOwnership.SyncDelivered(SyncOwned, landed, "P", "abc").Should().BeTrue();
        PartitionContentOwnership.SyncDelivered(InstallerOwned, landed, "P", "abc").Should().BeFalse(
            "an installer-owned partition is applied, not adopted");
        PartitionContentOwnership.SyncDelivered(Undetermined, landed, "P", "abc").Should().BeFalse(
            "an unobserved partition is not a synced one");
    }

    [Fact]
    public void TheModuleLane_LiftsTheOwnershipDecline_OnlyWhenTheSyncLandedTheServedContent()
    {
        var landed = SyncedModuleVersions.Of([new("P", "abc")]);
        RegistryUpdateReconciler.SyncConvergedDecline(SyncOwned, landed, "P", "abc").Should().BeNull(
            "code and content are of one tree — gate 1b has nothing to protect");
        RegistryUpdateReconciler.SyncConvergedDecline(SyncOwned, landed, "P", "newer").Should().NotBeNull(
            "the registry serves a newer bundle than the sync landed — landing it would split code from content");
        RegistryUpdateReconciler.SyncConvergedDecline(SyncOwned, landed, "P", null).Should().NotBeNull(
            "the broadcast lane names no candidate, so it keeps the hold");
        RegistryUpdateReconciler.SyncConvergedDecline(InstallerOwned, SyncedModuleVersions.Unknown, "P", "abc")
            .Should().BeNull("an installer-owned partition was never declined");
    }

    [Fact]
    public void AdoptedFromSync_MovesTheBooks_AndKeepsTheInstallTimeFields()
    {
        var record = new PackageManifest
        {
            Id = "P", Version = "1.0.0", ModuleVersion = "old", UpdatePolicy = PackageUpdatePolicy.Auto,
            AuthorizedBy = "system-security", HeldUpdate = "held: …", HeldSince = DateTimeOffset.UnixEpoch,
            HeldUpdateDispatch = "dispatched", HeldUpdateDispatchedAt = DateTimeOffset.UnixEpoch,
        };
        var candidate = new PackageManifest
        {
            Id = "P", Version = "1.1.0", ReleasedVersion = "1.1.0", ModuleVersion = "new", MinMeshVersion = "3.0.0-ci.9887",
            ManifestFiles = ImmutableSortedDictionary<string, string>.Empty.Add("P/index.json", "h"),
        };
        var now = DateTimeOffset.Parse("2026-10-04T00:00:00Z");
        var adopted = PackageUpdateReconciler.AdoptedFromSync(record, candidate, now);
        adopted.ModuleVersion.Should().Be("new");
        adopted.ReleasedVersion.Should().Be("1.1.0");
        adopted.MinMeshVersion.Should().Be("3.0.0-ci.9887");
        adopted.InstalledFiles.Should().ContainKey("P/index.json");
        adopted.InstalledAtUtc.Should().Be(now);
        adopted.HeldUpdate.Should().BeNull();
        adopted.HeldSince.Should().BeNull();
        adopted.HeldUpdateDispatch.Should().BeNull();
        adopted.UpdatePolicy.Should().Be(PackageUpdatePolicy.Auto, "policy is the administrator's, never the lane's");
        adopted.AuthorizedBy.Should().Be("system-security");
    }

    [Fact]
    public void OnlyAFloorHold_EndsWhenTheFloorIsMet()
    {
        var floorHold = PackagePlatformFloorGate.HeldSentence(
            new PackageManifest { Id = "AI", ReleasedVersion = "1.19.4" },
            new PlatformFloorVerdict(PlatformFloorKind.Held, "3.0.0-ci.9887", "3.0.0-ci.9885", null));
        PackagePlatformFloorGate.IsFloorHold(floorHold).Should().BeTrue();
        PackagePlatformFloorGate.IsFloorHold(PackageUpdateReconciler.SyncHoldSentence(
                new PackageManifest { Id = "AI", ReleasedVersion = "1.19.4", ModuleVersion = "a4" },
                SyncedModuleVersions.Of([new("AI", "a1")]), "AI"))
            .Should().BeFalse("a sync hold is re-decided by its own lane and keeps its since-when");
        PackagePlatformFloorGate.IsFloorHold(null).Should().BeFalse();
    }

    [Fact]
    public void TheInstaller_RefusesADifferentVersionAboveTheFloor_EvenOverAnExistingRecord()
    {
        PackagePlatformFloorGate.AllowedOverExistingRecord(true, "same", "same").Should().BeTrue(
            "a re-install of what is already here heals in place");
        PackagePlatformFloorGate.AllowedOverExistingRecord(true, "old", "new").Should().BeFalse(
            "a maintenance refresh must not land the very version the floor holds (the 09-27 breakage)");
        PackagePlatformFloorGate.AllowedOverExistingRecord(false, null, "new").Should().BeFalse(
            "a fresh install above the floor was always refused");
        PackagePlatformFloorGate.AllowedOverExistingRecord(true, null, null).Should().BeFalse(
            "no content identity is no evidence of sameness");
    }

    [Fact]
    public void TheSyncHoldSentence_NamesWhatTheSyncLanded_AndWhatItWaitsFor()
    {
        var candidate = new PackageManifest { Id = "AI", ReleasedVersion = "1.19.4", ModuleVersion = "085cc77d" };
        PackageUpdateReconciler.SyncHoldSentence(candidate, SyncedModuleVersions.Of([new("AI", "8b9aad84")]), "AI")
            .Should().StartWith("held: 1.19.4").And.Contain("8b9aad84").And.Contain("085cc77d");
        PackageUpdateReconciler.SyncHoldSentence(candidate, SyncedModuleVersions.Unknown, "AI")
            .Should().Contain("could not be read", "cannot-tell is said, never spelt as 'nothing'");
    }
}

/// <summary>
/// 🚨 <b>The first step of every test run</b> (maintainer, 2026-10-04: "should be always beginning
/// of each test run", "done by framework"). The decision, every arm; the header; and the catalog's
/// reading of a stale instance.
/// </summary>
public class TestRunPreflightRulesTest
{
    private static VersionsUnderTest Reading(params ModuleUnderTest[] modules) =>
        new("3.0.0-ci.9887", "1bafcb28526951fe", "3.0.0-ci.9898", modules.ToImmutableList(), "fabrikam");

    [Fact]
    public void AnUpToDateInstance_DoesNothing()
    {
        var verdict = TestRunPreflight.Decide(Reading(new ModuleUnderTest("AI", "1.19.4", null)),
            platformAtTarget: true, mayConverge: true, alreadyConverged: false);
        verdict.Kind.Should().Be(TestRunPreflightKind.UpToDate);
        verdict.Proceed.Should().BeTrue();
        verdict.Message.Should().Contain("nothing to converge");
    }

    [Fact]
    public void AStaleInstance_ConvergesFirst_WhenTheRunMayChangeIt()
    {
        var verdict = TestRunPreflight.Decide(Reading(new ModuleUnderTest("AI", "1.18.1", "1.19.4")),
            platformAtTarget: true, mayConverge: true, alreadyConverged: false);
        verdict.Kind.Should().Be(TestRunPreflightKind.Converge);
        verdict.Message.Should().Contain("AI 1.18.1 → 1.19.4");
    }

    [Fact]
    public void AStaleInstance_FailsNamingTheSkew_WhenTheRunMayNotChangeIt_OrConvergingDidNotCloseIt()
    {
        var refused = TestRunPreflight.Decide(
            Reading(new ModuleUnderTest("AI", "1.18.1", "1.19.4", "held: 1.19.4 needs platform ≥ 3.0.0-ci.9887")),
            platformAtTarget: true, mayConverge: false, alreadyConverged: false);
        refused.Kind.Should().Be(TestRunPreflightKind.Skew);
        refused.Proceed.Should().BeFalse("never silently test an old version");
        refused.Message.Should().Contain("AI 1.18.1 ≠ target 1.19.4").And.Contain("held: 1.19.4")
            .And.Contain("may not change it");

        var stillBehind = TestRunPreflight.Decide(Reading(new ModuleUnderTest("AI", "1.18.1", "1.19.4")),
            platformAtTarget: true, mayConverge: true, alreadyConverged: true);
        stillBehind.Kind.Should().Be(TestRunPreflightKind.Skew);
        stillBehind.Message.Should().Contain("did not close it");
    }

    [Fact]
    public void APlatformBehindTheTarget_IsAlwaysASkew_ATestRunNeverRollsAnImage()
    {
        var verdict = TestRunPreflight.Decide(Reading(), platformAtTarget: false, mayConverge: true, alreadyConverged: false);
        verdict.Kind.Should().Be(TestRunPreflightKind.Skew);
        verdict.Message.Should().Contain("3.0.0-ci.9887").And.Contain("3.0.0-ci.9898").And.Contain("never rolls");
    }

    [Fact]
    public void TheHeader_ListsThePlatform_TheTarget_AndEveryModuleVersion()
    {
        var header = Reading(new ModuleUnderTest("AI", "1.19.4", null), new ModuleUnderTest("Hosting", "1.53.0", "1.53.1"))
            .Header();
        header.Should().Contain("platform 3.0.0-ci.9887").And.Contain("core 1bafcb285").And.Contain("target 3.0.0-ci.9898")
            .And.Contain("AI 1.19.4").And.Contain("Hosting 1.53.0 → target 1.53.1").And.Contain("1 off-target");
        Reading(new ModuleUnderTest("AI", "1.19.4", null)).Header(all: false).Should().NotContain("  AI",
            "the short form lists only what is off-target");
        VersionsUnderTest.OfThisProcess().Header().Should().Contain("in-process mesh").And.Contain("no target known");
    }

    [Fact]
    public void TheCatalogReading_NamesTheTargetFromTheServedFeed()
    {
        var served = ImmutableList.Create(
            new ServedPackage("AI", "1.19.4", "085cc77d", "3.0.0-ci.9887"),
            new ServedPackage("Hosting", "1.53.1", "h2", "3.0.0-ci.9898"),
            new ServedPackage("Store", "1.20.0", "s1", null));
        var records = ImmutableList.Create(
            new PackageManifest { Id = "AI", ReleasedVersion = "1.19.4", ModuleVersion = "085cc77d" },
            new PackageManifest { Id = "Hosting", ReleasedVersion = "1.53.0", ModuleVersion = "h1", HeldUpdate = "held: …" },
            new PackageManifest { Id = "Local", Version = "0.1.0", ModuleVersion = "l1" });

        var (versions, platformAtTarget) = CatalogTestRunPreflight.Compose("3.0.0-ci.9887", "abc", served, records);
        versions.TargetPlatform.Should().Be("3.0.0-ci.9898", "the target platform is the newest floor CI stamped on a served package");
        platformAtTarget.Should().BeFalse();
        versions.Modules.Single(m => m.Id == "AI").AtTarget.Should().BeTrue();
        var hosting = versions.Modules.Single(m => m.Id == "Hosting");
        hosting.AtTarget.Should().BeFalse();
        hosting.Target.Should().Be("1.53.1");
        hosting.Held.Should().Be("held: …");
        versions.Modules.Single(m => m.Id == "Local").AtTarget.Should().BeTrue("a package the registry does not serve has no target");

        CatalogTestRunPreflight.Compose("3.0.0-ci.9898", "abc", served, records).PlatformAtTarget.Should().BeTrue();

        // 🚨 The ARMED cap (measured 2026-10-04: 9898 sealed, not armed — no roll could reach it).
        var (armed, armedAtTarget) = CatalogTestRunPreflight.Compose("3.0.0-ci.9887", "abc", served, records, latestArmed: "3.0.0-ci.9887");
        armed.TargetPlatform.Should().Be("3.0.0-ci.9887", "the target is the newest set the fleet can RUN");
        armedAtTarget.Should().BeTrue();
        armed.Modules.Single(m => m.Id == "Hosting").AtTarget.Should().BeTrue(
            "a served version whose floor is above the armed target is AWAITING ARMING, not behind");
        TargetSet.Standing("h1", served[1], "3.0.0-ci.9887").Should().Be(TargetStanding.AwaitingArming);
        TargetSet.Standing("h1", served[1], "3.0.0-ci.9898").Should().Be(TargetStanding.Behind);
        CatalogTestRunPreflight.Compose("3.0.0-ci.9898", "abc", null, records).Versions.TargetPlatform
            .Should().BeNull("no feed read yet is 'no target known', never 'at target'");
    }

    [Fact]
    public void TheTargetSet_PlatformIsTheNewestOrderedFloor()
    {
        TargetSet.Platform([
            new ServedPackage("A", "1", "a", "3.0.0-ci.9606"),
            new ServedPackage("B", "1", "b", "3.0.0-ci.9898"),
            new ServedPackage("C", "1", "c", "3.0.0-ci.0"),
            new ServedPackage("D", "1", "d", null),
        ]).Should().Be("3.0.0-ci.9898");
        TargetSet.Platform([]).Should().BeNull();
        TargetSet.SetsBehind("3.0.0-ci.9606", "3.0.0-ci.9898").Should().Be(292L);
        TargetSet.SetsBehind("3.0.0-ci.9900", "3.0.0-ci.9898").Should().Be(0L);
        TargetSet.PlatformAtTarget(null, "3.0.0-ci.9898").Should().BeFalse("an unreadable running version is not at target");
    }
}

/// <summary>The in-process run header, written by the monolith base before any test body.</summary>
public class MonolithRunHeaderTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public void TheBase_RecordsTheVersionsUnderTest_BeforeTheTestRuns()
    {
        VersionsUnderTest.Should().NotBeNull("the framework records the run's header in InitializeAsync");
        var versions = VersionsUnderTest ?? throw new InvalidOperationException("no versions under test were recorded");
        versions.Source.Should().Be("in-process mesh");
        versions.Commit.Should().Be(PlatformBuildInfo.CommitHash);
        versions.Header().Should().Contain("Versions under test");
    }
}

/// <summary>
/// The catalog preflight on a STALE mesh it may change, where the convergence pass cannot close the
/// gap (no registry is configured to land from): it tries, re-reads, and refuses naming the skew —
/// never a silent pass on an old version.
/// </summary>
public class CatalogTestRunPreflightTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    [Fact(Timeout = 300_000)]
    public async Task AStaleInstance_IsConvergedFirst_AndRefusedNamingTheSkew_WhenThatDoesNotCloseIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<CatalogTestRunPreflightTest>();
        await FloorHoldFixture.InstallV1(Mesh, logger, ct);
        var preflight = Mesh.ServiceProvider.GetRequiredService<ITestRunPreflight>();
        preflight.Should().BeOfType<CatalogTestRunPreflight>("the catalog registers the framework step");

        // No feed read yet: no target is known, so there is nothing to converge — and it says so.
        var unknown = await preflight.Prepare(mayConverge: true).FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(ct);
        unknown.Kind.Should().Be(TestRunPreflightKind.UpToDate);
        unknown.Versions.Modules.Should().Contain(m => m.Id == FloorHoldFixture.Package && m.Installed == FloorHoldFixture.V1Version);

        // The feed now serves V2: the instance is stale.
        await WriteLedger(new ServedPackage(FloorHoldFixture.Package, FloorHoldFixture.V2Version, FloorHoldFixture.V2Hash, null), ct);
        var stale = await preflight.Prepare(mayConverge: true).FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await(ct);
        stale.Kind.Should().Be(TestRunPreflightKind.Skew, "the pass ran and could not land V2 — the run must not test V1 as if current");
        stale.Message.Should().Contain(FloorHoldFixture.Package).And.Contain(FloorHoldFixture.V2Version).And.Contain("did not close it");
        stale.Versions.Header().Should().Contain($"{FloorHoldFixture.Package} {FloorHoldFixture.V1Version} → target {FloorHoldFixture.V2Version}");

        var mayNot = await preflight.Prepare(mayConverge: false).FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(ct);
        mayNot.Kind.Should().Be(TestRunPreflightKind.Skew);
        mayNot.Message.Should().Contain("may not change it");
    }

    private async Task WriteLedger(ServedPackage served, CancellationToken ct)
    {
        var node = MeshNode.Satellite(RegistryUpdateReconciler.LedgerId, PackageInstaller.InstalledPartition) with
        {
            Name = "Registry reconcile ledger",
            NodeType = RegistryUpdateReconciler.LedgerNodeType,
            State = MeshNodeState.Active,
            Content = new RegistryReconcileLedger
            {
                Registries = [new RegistryReconcileEntry { Url = "https://registry.example.com", Name = "test", Served = [served] }],
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        await access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateOrUpdateNode(node))
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).Await(ct);
    }
}
