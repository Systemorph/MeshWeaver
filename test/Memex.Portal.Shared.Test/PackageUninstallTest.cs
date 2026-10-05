#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>The pure rules of a package uninstall.</summary>
public class PackageUninstallRulesTest
{
    [Fact]
    public void AnUninstall_NeedsAPackageAndAReason()
    {
        PackageUninstall.Validate(new PackageUninstallRequest { Package = "BuildServer", Reason = "x" }).Should().BeNull();
        PackageUninstall.Validate(new PackageUninstallRequest { Package = "", Reason = "x" }).Should().Contain("package id");
        PackageUninstall.Validate(new PackageUninstallRequest { Package = "Plugins/X", Reason = "x" }).Should().Contain("not a package id");
        PackageUninstall.Validate(new PackageUninstallRequest { Package = "X", Reason = " " }).Should().Contain("reason");
    }

    [Fact]
    public void RowsAreCountedPerTable_AndOnlyPeopleCountAsUserData()
    {
        PackageUninstallExecutor.TableOf("P", "P").Should().Be("mesh_nodes");
        PackageUninstallExecutor.TableOf("P", "P/Doc/Intro").Should().Be("mesh_nodes");
        PackageUninstallExecutor.TableOf("P", "P/_Access/Admins").Should().Be("_Access");
        PackageUninstallExecutor.TableOf("P", "P/Doc/_Thread/t1").Should().Be("_Thread");

        PackageUninstallExecutor.IsUserCreated(WellKnownUsers.System).Should().BeFalse("the installer writes as System");
        PackageUninstallExecutor.IsUserCreated(null).Should().BeFalse();
        PackageUninstallExecutor.IsUserCreated("alice").Should().BeTrue();
    }
}

/// <summary>
/// 🚨 <b>Uninstall, end to end — two phases.</b> Phase 1 retires the module (one restart here: no
/// live loader), removes the install record and blocks re-install, and only PREVIEWS the data;
/// phase 2 drops it only on the requester's exact confirmation. Real request node, executor,
/// install records, landing service, self-updater restart path and the platform's partition teardown.
/// </summary>
public class PackageUninstallTest(ITestOutputHelper output) : ModuleReloadScenario(output)
{
    private const string Requester = "uninstall-requester";

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    /// <summary>The package's content partition, written as System — what the installer would have landed.</summary>
    private async Task Content(CancellationToken ct)
    {
        foreach (var node in new[]
                 {
                     MeshNode.FromPath(Package) with { NodeType = "Markdown", Name = Package, State = MeshNodeState.Active },
                     MeshNode.FromPath($"{Package}/Guide") with { NodeType = "Markdown", Name = "Guide", State = MeshNodeState.Active },
                 })
            await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(node)).Timeout(TestTimeouts.Convergence).Await(ct);
        (await Exists($"{Package}/Guide", ct)).Should().BeTrue("the premise: the package's content exists");
    }

    private Task<bool> Exists(string path, CancellationToken ct) =>
        Storage.Read(path, Mesh.JsonSerializerOptions).Take(1).DefaultIfEmpty(null)
            .Select(n => n is not null).Timeout(TestTimeouts.Convergence).Await(ct);

    private async Task<string> Uninstall(string package, CancellationToken ct)
    {
        var ticket = await PackageUninstall.Request(Mesh, new PackageUninstallRequest
            {
                Package = package,
                Reason = "retired from Plugins main",
                RequestedBy = Requester,
            })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        ticket.Refusal.Should().BeNull();
        ticket.Path.Should().NotBeNull();
        return ticket.Path ?? "";
    }

    private Task<PackageUninstallRequest> Await(string path, Func<PackageUninstallRequest, bool> until, CancellationToken ct) =>
        Access.RunAsSystem(() => Mesh.GetMeshNodeStream(path))
            .Select(n => n.ContentAs<PackageUninstallRequest>(Mesh.JsonSerializerOptions))
            .OfType<PackageUninstallRequest>()
            .Where(until)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    [Fact(Timeout = 240_000)]
    public async Task PhaseOneRetainsTheData_AWrongConfirmationIsRefused_TheRightOneDropsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        await Content(ct);

        var path = await Uninstall(Package, ct);
        var preview = await Await(path, r => r.Status == PackageUninstallStatus.AwaitingConfirmation || PackageUninstallStatus.IsTerminal(r.Status), ct);

        preview.Status.Should().Be(PackageUninstallStatus.AwaitingConfirmation, preview.Failure ?? "");
        preview.ConfirmationRequired.Should().Be(Package);
        var p = preview.Partitions.Single();
        p.Partition.Should().Be(Package);
        p.RowsByTable["mesh_nodes"].Should().Be(2, "the preview counts exactly what phase 2 would drop");
        p.Unmeasured.Should().NotBeEmpty("what cannot be counted is said");
        preview.ModuleOutcome.Should().Contain("one restart requested");
        Updater.Restarts.Should().Be(1, "the loaded module unloads by exactly one restart (no live loader here)");
        (await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct)).Should().BeFalse("the install record is removed");
        (await PackageUninstallExecutor.UninstalledHere(Mesh).Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Contain(Package, "no unattended pass may install it again");

        // ── Negative control: NO confirmation → the data is retained. ──────────────────────────
        (await Exists($"{Package}/Guide", ct)).Should().BeTrue("without a confirmation nothing is dropped");

        // ── A WRONG confirmation is refused by name, and the data is still retained. ───────────
        (await PackageUninstall.Confirm(Mesh, path, "Plugins", Requester).Timeout(TestTimeouts.Convergence).Await(ct)).Accepted.Should().BeTrue();
        var refused = await Await(path, r => r.ConfirmationRefusal is not null, ct);
        refused.ConfirmationRefusal.Should().Contain("'Plugins' does not match the required 'ReloadPkg'");
        refused.Status.Should().Be(PackageUninstallStatus.AwaitingConfirmation);
        (await Exists($"{Package}/Guide", ct)).Should().BeTrue();

        // ── Someone other than the requester is refused too. ──────────────────────────────────
        await PackageUninstall.Confirm(Mesh, path, Package, "someone-else").Timeout(TestTimeouts.Convergence).Await(ct);
        (await Await(path, r => r.ConfirmationRefusal?.Contains("someone-else") == true, ct)).Status
            .Should().Be(PackageUninstallStatus.AwaitingConfirmation);
        (await Exists($"{Package}/Guide", ct)).Should().BeTrue();

        // ── The requester's exact confirmation drops the partition. ────────────────────────────
        await PackageUninstall.Confirm(Mesh, path, Package, Requester).Timeout(TestTimeouts.Convergence).Await(ct);
        var done = await Await(path, r => PackageUninstallStatus.IsTerminal(r.Status), ct);
        done.Status.Should().Be(PackageUninstallStatus.Done, done.Failure ?? "");
        done.ConfirmedBy.Should().Be(Requester);
        done.ConfirmedAt.Should().NotBeNull();
        done.Partitions.Single().TornDown.Should().BeTrue();
        done.Partitions.Single().TeardownOutcome.Should().Contain("Admin/Partition/ReloadPkg");
        (await Exists($"{Package}/Guide", ct)).Should().BeFalse("the partition's content is gone");
        (await Exists(Package, ct)).Should().BeFalse("and its root");
        Updater.Restarts.Should().Be(1, "phase 2 restarts nothing");
    }

    [Fact(Timeout = 240_000)]
    public async Task ARePassDoesNotBringItBack_UnlessAPersonInstallsItAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        await Content(ct);
        var path = await Uninstall(Package, ct);
        await Await(path, r => r.Status == PackageUninstallStatus.AwaitingConfirmation, ct);

        (await PackageUninstallExecutor.UninstalledHere(Mesh).Timeout(TestTimeouts.Convergence).Await(ct)).Should().Contain(Package);

        // Negative control: a person installs it again (an install record exists) → no longer blocked.
        var reinstalled = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/{Package}") with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = Package,
            State = MeshNodeState.Active,
            Content = new PackageManifest { Id = Package, Name = Package, Version = "1.1.0", TargetPartition = Package, Module = Module },
        };
        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(reinstalled)).Timeout(TestTimeouts.Convergence).Await(ct);
        (await PackageUninstallExecutor.UninstalledHere(Mesh).Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().NotContain(Package, "installing it again lifts the block");
    }

    [Fact(Timeout = 240_000)]
    public async Task AnUnknownPackage_IsRefusedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = await Uninstall("NotInstalledHere", ct);
        var red = await Await(path, r => PackageUninstallStatus.IsTerminal(r.Status), ct);
        red.Status.Should().Be(PackageUninstallStatus.Failed);
        red.Failure.Should().Contain("'NotInstalledHere' is not an installed package here");
        Updater.Restarts.Should().Be(0);

        // A confirmation for a request that does not AWAIT one is refused and records nothing —
        // the preview is what a confirmation answers (#6124 review).
        var early = await PackageUninstall.Confirm(Mesh, path, "NotInstalledHere", Requester).Timeout(TestTimeouts.Convergence).Await(ct);
        early.Accepted.Should().BeFalse();
        early.Refusal.Should().Contain("does not await a confirmation");
        (await Await(path, _ => true, ct)).Confirmation.Should().BeNull("nothing was recorded");
    }

    [Fact(Timeout = 240_000)]
    public async Task ARequestNamingNoRequester_CannotBeConfirmedByAnyone()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        await Content(ct);
        var ticket = await PackageUninstall.Request(Mesh, new PackageUninstallRequest
            {
                Package = Package,
                Reason = "retired from Plugins main",
                RequestedBy = null,
            })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        ticket.Path.Should().NotBeNull(ticket.Refusal ?? "");
        var path = ticket.Path ?? "";
        var preview = await Await(path, r => r.Status == PackageUninstallStatus.AwaitingConfirmation || PackageUninstallStatus.IsTerminal(r.Status), ct);
        preview.Status.Should().Be(PackageUninstallStatus.AwaitingConfirmation, preview.Failure ?? "");
        preview.AwaitingConfirmationAt.Should().NotBeNull("the preview's instant is stamped");

        (await PackageUninstall.Confirm(Mesh, path, Package, "any-admin").Timeout(TestTimeouts.Convergence).Await(ct)).Accepted.Should().BeTrue();
        var refused = await Await(path, r => r.ConfirmationRefusal is not null, ct);
        refused.ConfirmationRefusal.Should().Contain("names no requester");
        refused.Status.Should().Be(PackageUninstallStatus.AwaitingConfirmation);
        (await Exists($"{Package}/Guide", ct)).Should().BeTrue("nothing is dropped without a named requester");
    }

    [Fact(Timeout = 240_000)]
    public async Task ASharedPartition_IsRefused_AndNothingIsTouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        await Content(ct);
        var other = MeshNode.FromPath($"{PackageInstaller.InstalledPartition}/OtherPkg") with
        {
            NodeType = PackageInstaller.PackageNodeType,
            Name = "OtherPkg",
            State = MeshNodeState.Active,
            Content = new PackageManifest { Id = "OtherPkg", Name = "OtherPkg", Version = "1.0.0", TargetPartition = Package },
        };
        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(other)).Timeout(TestTimeouts.Convergence).Await(ct);

        var path = await Uninstall(Package, ct);
        var red = await Await(path, r => PackageUninstallStatus.IsTerminal(r.Status), ct);
        red.Status.Should().Be(PackageUninstallStatus.Failed);
        red.Failure.Should().Contain("shared with the installed package(s) OtherPkg");
        (await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct)).Should().BeTrue("nothing was touched");
        (await Exists($"{Package}/Guide", ct)).Should().BeTrue();
        Updater.Restarts.Should().Be(0);
    }

    [Fact(Timeout = 240_000)]
    public async Task APartitionHoldingUserData_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunningVersionOne(ct);
        await Content(ct);
        // A node a PERSON created in the package's partition.
        var mine = MeshNode.FromPath($"{Package}/MyNotes") with
        {
            NodeType = "Markdown", Name = "My notes", State = MeshNodeState.Active, CreatedBy = "alice",
        };
        await Access.RunAsSystem(() => NodeFactory.CreateOrUpdateNode(mine)).Timeout(TestTimeouts.Convergence).Await(ct);
        var stored = await Storage.Read(mine.Path, Mesh.JsonSerializerOptions).Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        Assert.SkipWhen(stored?.CreatedBy != "alice",
            "the store stamps CreatedBy itself here, so a person's node cannot be simulated through this write path");

        var path = await Uninstall(Package, ct);
        var red = await Await(path, r => PackageUninstallStatus.IsTerminal(r.Status), ct);
        red.Status.Should().Be(PackageUninstallStatus.Failed);
        red.Failure.Should().Contain("created by people").And.Contain("MyNotes by alice");
        (await Exists($"{PackageInstaller.InstalledPartition}/{Package}", ct)).Should().BeTrue("nothing was touched");
    }
}
