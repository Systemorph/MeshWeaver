#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// THE PHANTOM INSTALL RECORD (#840). When a package leaves the registry — the live case was a
/// course folder renamed <c>KmuBasics</c> → <c>AgenticOffice</c>, which makes it a new product id —
/// its install record <c>Plugins/{oldId}</c> had no route out of the mesh at all:
///
/// <list type="number">
///   <item><c>Plugins/_Policy</c> caps <c>create/update/delete</c> at <c>false</c> for EVERY caller,
///     a platform admin holding an Admin assignment on the <c>Plugins</c> partition included. That
///     is correct — only the installer writes there, under system impersonation.</item>
///   <item>The only system-identity removal was the catalog card's Uninstall, and cards are driven
///     by the REGISTRY's package list — a package that left the registry has no card.</item>
///   <item>So the record persisted forever, rendering publicly (<c>publicRead</c>) as an
///     "installed" product that no longer exists.</item>
/// </list>
///
/// <para>The fix is the missing SURFACE, never a weaker policy: the catalog additionally lists
/// install records the source no longer offers
/// (<see cref="CatalogLayoutAreas.Orphaned"/>) and offers a global-admin-only action that runs the
/// installer's system-impersonated removal
/// (<see cref="PackageInstaller.RemoveInstalledRecord"/>) — the same identity that wrote the
/// record.</para>
///
/// <para>Built on <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> with the PRODUCTION admin
/// shape (Admin on the Admin partition), plus the Admin assignment on <c>Plugins</c> the issue's
/// reproduction granted — so the "still denied" step is exercised for real rather than assumed.</para>
/// </summary>
public class OrphanedInstallRecordTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The production-shaped platform admin: Admin on the Admin partition.</summary>
    private const string PlatformAdmin = "orphan-admin";

    /// <summary>The package that later leaves the registry.</summary>
    private const string DepartedId = "kmu-basics";

    /// <summary>The package that stays — so the available list is non-empty and orphans are real.</summary>
    private const string StayingId = "agentic-office";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            // The exact grant shape GrantPlatformAdmin writes …
            .AddMeshNodes(AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", "Admin"))
            // … PLUS the Admin assignment on the Plugins partition the issue's repro granted before
            // concluding the record was undeletable. It must not change the outcome: the partition
            // policy caps every role.
            .AddMeshNodes(AssignmentNodeFactory.UserRole(
                PlatformAdmin, "Admin", PackageInstaller.InstalledPartition));

    private static PackageManifest Manifest(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = PackageKind.Content,
        TargetPartition = id,
        SourceFolder = id,
        Version = "1.0.0",
    };

    private Task<InstallResult> Install(string id) =>
        PackageInstaller.Install(
                Mesh, Manifest(id), [new PackageFile($"{id}/Doc.md", $"# {id}")], "HEAD",
                authorizingUserId: PlatformAdmin)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();

    [Fact(Timeout = 180_000)]
    public async Task ARecordWithNoRegistryCounterpart_IsOffered_AndActuallyRemoved()
    {
        await Install(StayingId);
        await Install(DepartedId);

        var recordPath = $"{PackageInstaller.InstalledPartition}/{DepartedId}";
        var record = await Read(recordPath);
        record.Should().NotBeNull("the install must have written the record the issue is about");

        // ── the lock, verified rather than assumed ────────────────────────────────────────────
        // A platform admin who ALSO holds an Admin assignment on the Plugins partition — the exact
        // grant the issue's reproduction added before concluding the record was undeletable — is
        // still denied Delete: the partition policy caps the write permissions of EVERY role.
        await Mesh.GetEffectivePermissions(PackageInstaller.InstalledPartition, PlatformAdmin)
            .Should().Within(TimeSpan.FromSeconds(30))
            .Match(
                p => !p.HasFlag(Permission.Delete)
                     && !p.HasFlag(Permission.Create)
                     && !p.HasFlag(Permission.Update)
                     && p.HasFlag(Permission.Read),
                "the Plugins policy caps every caller's writes — that is the lock #840 describes, "
                + "and the fix must not relax it");

        // ── the missing surface ───────────────────────────────────────────────────────────────
        // The registry now offers only the renamed product. The departed record has no card, and
        // this is the list that gives it one.
        var installed = new[] { record!, (await Read($"{PackageInstaller.InstalledPartition}/{StayingId}"))! };
        var available = new[] { Manifest(StayingId) };

        var orphans = CatalogLayoutAreas.Orphaned(available, installed, Mesh.JsonSerializerOptions);
        orphans.Select(o => o.Id).ToList().Should().Equal([DepartedId],
            "an install record the source no longer offers is exactly what has no card today");

        // The destructive guess this must never make: an EMPTY available list means "the registry
        // offers nothing" and "listing it failed" indistinguishably, so nothing is orphaned.
        CatalogLayoutAreas.Orphaned([], installed, Mesh.JsonSerializerOptions)
            .Should().BeEmpty("an unreachable registry must never offer to remove every record");

        // ── the removal actually removes ──────────────────────────────────────────────────────
        var removed = await PackageInstaller.RemoveInstalledRecord(Mesh, DepartedId)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).ToTask();
        removed.Should().BeTrue("the sanctioned route runs as System — the identity that wrote it");

        (await Read(recordPath)).Should().BeNull(
            "the phantom record must be GONE from storage, not merely hidden from a view");

        // The rest of the registry is untouched — a removal is per record, never a sweep.
        (await Read($"{PackageInstaller.InstalledPartition}/{StayingId}")).Should().NotBeNull();

        // The installed CONTENT is a separate lifecycle: removing the record must not delete the
        // partition it installed (the issue's repro deletes that separately, by hand).
        (await Read($"{DepartedId}/Doc")).Should().NotBeNull(
            "removing an install record must not touch the content it recorded");
    }

    /// <summary>
    /// Removing a record that is not there reports the miss by NAME rather than pretending to
    /// succeed — the removal is a thin pass-through to the mesh's delete, whose contract is to fault
    /// on an absent node. The orphan list is rendered live, so this is the second admin clicking a
    /// card the first one already removed: the catalog's error handler logs it, and nothing wedges.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task RemovingAnAbsentRecord_FailsByName_RatherThanReportingSuccess()
    {
        var exception = await Record.ExceptionAsync(() =>
            PackageInstaller.RemoveInstalledRecord(Mesh, "never-installed")
                .FirstAsync().Timeout(TimeSpan.FromSeconds(60)).ToTask());

        exception.Should().NotBeNull("a record that is not there was not removed");
        exception!.Message.Should().Contain($"{PackageInstaller.InstalledPartition}/never-installed",
            "the failure must name the record, not just fail");
    }
}
