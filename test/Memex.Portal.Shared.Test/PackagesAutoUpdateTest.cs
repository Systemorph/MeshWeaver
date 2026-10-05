#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Policy <c>packages-auto-update</c> — the pure rules: a fresh install is Auto whatever the
/// deployment's legacy defaults say; a SEEDED reminder-only record migrates to Auto; an
/// administrator's explicit choice and a pin are kept and named.
/// </summary>
public class PackagesAutoUpdateRulesTest
{
    [Fact]
    public void AFreshInstall_IsAuto_WhateverTheDeploymentDefaultSaid()
    {
        PackageInstaller.SeedUpdatePolicy(null, null).Should().Be(PackageUpdatePolicy.Auto);
        PackageInstaller.SeedUpdatePolicy(null, new PluginCatalogOptions()).Should().Be(PackageUpdatePolicy.Auto,
            "AutoUpdateByDefault=false is the CLR default — 'nobody said anything' — and no longer means reminder-only");
        PackageInstaller.SeedUpdatePolicy(null, new PluginCatalogOptions { DefaultUpdatePolicy = PackageUpdatePolicy.Notify })
            .Should().Be(PackageUpdatePolicy.Auto, "a deployment-wide notify default is exactly what the policy retires");
    }

    [Fact]
    public void ASeededReminderOnlyRecord_Migrates_AndADeliberateChoiceIsKept()
    {
        var legacy = new PackageManifest { Id = "A" };                                         // autoUpdate:false, no policy
        var seeded = new PackageManifest { Id = "B", UpdatePolicy = PackageUpdatePolicy.Notify }; // the old seed
        var chosen = new PackageManifest { Id = "C", UpdatePolicy = PackageUpdatePolicy.Notify, UpdatePolicySetAt = DateTimeOffset.UtcNow };
        var pinned = new PackageManifest { Id = "D", UpdatePolicy = PackageUpdatePolicy.None };
        var auto = new PackageManifest { Id = "E", UpdatePolicy = PackageUpdatePolicy.Auto, AutoUpdate = true };

        PackageAutoUpdateMigration.Migrates(legacy).Should().BeTrue();
        PackageAutoUpdateMigration.Migrates(seeded).Should().BeTrue();
        PackageAutoUpdateMigration.Migrated(seeded).EffectiveUpdatePolicy.Should().Be(PackageUpdatePolicy.Auto);
        PackageAutoUpdateMigration.Migrated(legacy).AutoUpdate.Should().BeTrue("the legacy flag is kept in step");

        // Negative controls: the opt-outs the policy keeps — named, never migrated.
        PackageAutoUpdateMigration.Migrates(chosen).Should().BeFalse();
        PackageAutoUpdateMigration.KeptOptOut(chosen).Should().Contain("chosen by an administrator");
        PackageAutoUpdateMigration.Migrates(pinned).Should().BeFalse();
        PackageAutoUpdateMigration.KeptOptOut(pinned).Should().Contain("pinned");
        PackageAutoUpdateMigration.Migrates(auto).Should().BeFalse("already Auto — the migration is idempotent");

        // A re-stamp of an existing record follows the same rule.
        PackageInstaller.SeedUpdatePolicy(seeded, null).Should().Be(PackageUpdatePolicy.Auto);
        PackageInstaller.SeedUpdatePolicy(chosen, null).Should().Be(PackageUpdatePolicy.Notify);
        PackageInstaller.SeedUpdatePolicy(pinned, null).Should().Be(PackageUpdatePolicy.None);
    }
}

/// <summary>
/// 🚨 <b>Policy <c>packages-auto-update</c>, end to end, with the platform FIXED</b>: no platform
/// build, no image roll, no publication seal, no seal for the running framework identity — and a
/// partition whose SYNC SOURCE still carries the old content. The only inputs are: the registry
/// published a newer version, and its declared floor is met. The registry's own reconcile pass
/// (<see cref="RegistryUpdateReconciler.ReconcileNow"/>) lands it and files the activation through
/// the module reload path — exactly one automatic restart here, since this process has no live
/// loader — with no human step.
/// </summary>
public class PackagesAutoUpdateTest(ITestOutputHelper output) : ModuleReloadScenario(output)
{
    private RegistryUpdateReconciler Reconciler => Mesh.ServiceProvider.GetRequiredService<RegistryUpdateReconciler>();

    /// <summary>The registry FEED lists the package at the content identity installed here (so the
    /// content lane has nothing to do) — the module lane is what moves.</summary>
    private void PublishInFeed(string? floor = null, string moduleVersion = "content-v1") =>
        Registry.Feed =
        [
            new PackageManifest
            {
                Id = Package, Name = Package, Version = "1.0.0", ModuleVersion = moduleVersion,
                TargetPartition = Package, Module = Module, MinMeshVersion = floor,
            },
        ];

    private Task Ready(CancellationToken ct) =>
        Reconciler.BootReconciled.DefaultIfEmpty().Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>The partition's SYNC SOURCE carries the OLD content — under the retired gate this
    /// held the module until the platform sealed the content repo.</summary>
    private async Task SyncOwnedAtOldContent(CancellationToken ct)
    {
        var config = new GitHubSyncConfig
        {
            RepositoryUrl = "https://github.com/Systemorph/Example",
            Branch = "main",
            ModuleVersions = ImmutableDictionary<string, string>.Empty.Add(Package, "content-v0"),
        };
        await Access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(
                new MeshNode(GitHubSyncService.ConfigId, Package)
                {
                    Name = "GitHub Sync",
                    NodeType = GitHubSyncService.ConfigNodeType,
                    State = MeshNodeState.Active,
                    Content = config,
                }).Take(1))
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var owner = await PartitionContentOwnership.Observe(Mesh, Package).Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        owner.Owner.Should().Be(PartitionContentOwner.SyncSource, "the premise: the partition is sync-owned");
    }

    private Task<PackageManifest> Record(Func<PackageManifest, bool> until, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream($"{PackageInstaller.InstalledPartition}/{Package}"))
            .Select(n => n.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions))
            .Where(m => m is not null && until(m))
            .Select(m => m!)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<ImmutableList<(string Path, ModuleReloadRequest Request)>> Requests(CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.ServiceProvider.GetRequiredService<IMeshService>()
                .Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{ModuleReloadRequest.Namespace} scope:children nodeType:{ModuleReloadRequest.NodeType}"))
                .Take(1))
            .Select(change => change.Items
                .Select(n => (n.Path, Request: n.ContentAs<ModuleReloadRequest>(Mesh.JsonSerializerOptions)!))
                .ToImmutableList())
            .Timeout(TestTimeouts.Convergence).Await(ct);

    [Fact(Timeout = 240_000)]
    public async Task ANewerCompatibleVersion_IsInstalledAndActivated_WithNoHumanStep_IndependentOfSyncAndSeal()
    {
        var ct = TestContext.Current.CancellationToken;
        await Ready(ct);
        // Installed on the RETIRED reminder-only default (autoUpdate:false, no policy).
        await RunningVersionOne(ct, m => m with { ModuleVersion = "content-v1" });
        await SyncOwnedAtOldContent(ct);

        // The migration moves the seeded reminder-only record to Auto.
        var pass = await PackageAutoUpdateMigration.Run(Mesh).Timeout(TestTimeouts.Convergence).Await(ct);
        (await Record(r => r.EffectiveUpdatePolicy == PackageUpdatePolicy.Auto, ct)).UpdatePolicy
            .Should().Be(PackageUpdatePolicy.Auto, "a record previously on notify-only is migrated to Auto");
        pass.KeptOptOuts.Should().BeEmpty();

        // 1.2.0 is PUBLISHED, compatible. Nothing else happens: no platform build, no seal, the
        // sync still holds the old content.
        Registry.Serve("1.2.0", floor: FloorFixture.Below);
        PublishInFeed();
        await Reconciler.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);

        Head().Version.Should().Be("1.2.0", "the published compatible module landed with no human step");
        var filed = await Requests(ct);
        filed.Should().ContainSingle("ONE activation request for the wave");
        var activated = await AwaitRequest(filed[0].Path,
            r => r.Status == ModuleReloadStatus.AwaitingRestart && r.ActivationDetail?.StartsWith(ModuleRestartKinds.Restarted) == true, ct);
        activated.Reason.Should().Contain("packages-auto-update");
        Updater.Restarts.Should().Be(1, "activated by exactly one automatic restart");

        // A SECOND publication minutes later rides the restart already on its way — still one.
        Registry.Serve("1.3.0", floor: FloorFixture.Below);
        await Reconciler.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);
        Head().Version.Should().Be("1.3.0");
        var second = (await Requests(ct)).Single(r => r.Path != filed[0].Path);
        var rode = await AwaitRequest(second.Path,
            r => r.Log.Any(l => l.Contains("riding it") || l.Contains("restart Restarted")) || ModuleReloadStatus.IsTerminal(r.Status), ct);
        rode.ActivationDetail.Should().Contain("rides the ONE restart");
        Updater.Restarts.Should().Be(1, "two waves before the restart happened → still exactly one restart");
    }

    /// <summary>Negative control: an administrator's DELIBERATE notify choice is kept — the same
    /// publication lands nothing and files nothing.</summary>
    [Fact(Timeout = 240_000)]
    public async Task ADeliberateOptOut_IsKept_AndNothingLands()
    {
        var ct = TestContext.Current.CancellationToken;
        await Ready(ct);
        await RunningVersionOne(ct, m => m with
        {
            ModuleVersion = "content-v1",
            UpdatePolicy = PackageUpdatePolicy.Notify,
            UpdatePolicySetAt = DateTimeOffset.UtcNow,
        });

        var pass = await PackageAutoUpdateMigration.Run(Mesh).Timeout(TestTimeouts.Convergence).Await(ct);
        pass.Migrated.Should().BeEmpty();
        pass.KeptOptOuts.Should().ContainSingle().Which.Should().Contain("chosen by an administrator");

        Registry.Serve("1.2.0", floor: FloorFixture.Below);
        PublishInFeed();
        await Reconciler.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);

        Head().Version.Should().Be("1.1.0");
        Registry.Downloads.Should().Equal(["1.1.0"]);
        (await Requests(ct)).Should().BeEmpty();
        Updater.Restarts.Should().Be(0);
    }

    /// <summary>An incompatible floor: declined BY NAME on the record, nothing downloaded or
    /// activated, and the running version keeps serving.</summary>
    [Fact(Timeout = 240_000)]
    public async Task AnIncompatibleFloor_IsDeclinedByName_AndTheRunningVersionKeepsServing()
    {
        var ct = TestContext.Current.CancellationToken;
        await Ready(ct);
        await RunningVersionOne(ct, m => m with { ModuleVersion = "content-v1", UpdatePolicy = PackageUpdatePolicy.Auto, AutoUpdate = true });

        Registry.Serve("1.2.0", floor: FloorFixture.Above);
        PublishInFeed(floor: FloorFixture.Above, moduleVersion: "content-v2");
        await Reconciler.ReconcileNow().Timeout(TestTimeouts.Convergence).Await(ct);

        var held = await Record(r => r.HeldUpdate is not null, ct);
        held.HeldUpdate.Should().Contain(FloorFixture.Above).And.Contain(FloorFixture.Running);
        Head().Version.Should().Be("1.1.0", "the running version keeps serving");
        Registry.Downloads.Should().Equal(["1.1.0"], "the held bundle is not downloaded");
        (await Requests(ct)).Should().BeEmpty("nothing landed, so nothing is activated");
        Updater.Restarts.Should().Be(0);
    }
}
