#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Pins #811(A): the <c>Plugins</c> install-records partition must be readable by a REAL signed-in
/// principal on a freshly bootstrapped mesh — via the read-only <c>PublicRead</c>
/// <c>PartitionAccessPolicy</c> that <see cref="PluginCatalogConfigurationExtensions.AddPluginCatalog"/>
/// registers, the same shape every other built-in catalog ships.
///
/// <para>The principal deliberately has the PRODUCTION shape (post-#804), which no other catalog
/// test exercises: a platform admin holding ONLY the correctly-scoped Admin-partition grant
/// (<c>Admin/_Access</c>, <c>MainNode="Admin"</c> — the exact node
/// <c>UserOnboardingService.GrantPlatformAdmin</c> writes), no root-scope grant and no
/// <c>Roles=["Admin"]</c> claim. Such a grant is never on the <c>Plugins</c> scope walk, and the
/// install records are written under <c>ImpersonateAsSystem</c> so no creator grant is ever minted
/// — without the policy, the settings tab's installed-state query
/// (<c>CatalogLayoutAreas.ObserveInstalled</c>, <c>path:Plugins scope:children</c>) is denied for
/// the very admin the tab is gated on. The default <c>MonolithMeshTestBase</c> setup would mask
/// all of this: it seeds a root-scope <c>Public → Admin</c> grant AND a circuit identity carrying
/// <c>Roles=["Admin"]</c> — the very claim mechanism #804 removed — so this test builds on
/// <see cref="MonolithMeshTestBase.ConfigureMeshBase"/> instead.</para>
/// </summary>
public class PluginsPartitionReadableTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The production-shaped platform admin: Admin on the Admin partition, nothing else.</summary>
    private const string PlatformAdmin = "e2e-admin";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            // The exact grant shape GrantPlatformAdmin writes: an AccessAssignment at
            // Admin/_Access with MainNode="Admin" (Admin role scoped to the Admin partition).
            .AddMeshNodes(AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", "Admin"));

    [Fact(Timeout = 120000)]
    public async Task PlatformAdmin_WithOnlyAdminScopedGrant_ReadsInstallRecords_OnFreshMesh()
    {
        // The seeded grant really is the production platform-admin shape …
        await Mesh.IsGlobalAdmin(PlatformAdmin).Should().Match(isAdmin => isAdmin);

        // … and NOT a data superuser: outside the catalog the principal holds nothing, so the
        // read below can only come from the partition policy (a root-scope grant sneaking back
        // into this fixture would fail here and void the pin).
        await Mesh.GetEffectivePermissions(TestPartition, PlatformAdmin)
            .Should().Match(p => p == Permission.None);

        // Install a package on the fresh mesh — the records land in "Plugins" under the System
        // identity (PackageInstaller.Upsert), exactly as in production, so no creator grant exists.
        var manifest = new PackageManifest
        {
            Id = "readable-pack",
            Name = "Readable Pack",
            Kind = PackageKind.Content,
            TargetPartition = "ReadableTarget",
            SourceFolder = "readable-pack",
            Version = "1.0.0",
        };
        var files = new[] { new PackageFile("readable-pack/Doc.md", "# Doc\n\nInstalled for the readability pin.") };
        var result = await PackageInstaller.Install(Mesh, manifest, files, "HEAD").FirstAsync().ToTask();
        result.Written.Should().Be(1);

        // The EXACT installed-state query the settings tab issues (CatalogLayoutAreas.
        // ObserveInstalled), evaluated under the admin's identity — RLS filters unreadable nodes
        // out of the result set, so without the PublicRead policy this set stays empty forever.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{PackageInstaller.InstalledPartition} scope:children nodeType:{PackageInstaller.PackageNodeType}",
                PlatformAdmin))
            .Should().Within(TimeSpan.FromSeconds(30))
            .Match(
                change => change.Items.Any(n =>
                    n.Path == $"{PackageInstaller.InstalledPartition}/readable-pack"),
                "a signed-in platform admin must see the install records the plugin-catalog settings tab queries");

        // Read-only: the policy grants exactly Read — its write caps deny Create/Update/Delete
        // (and Comment/Thread) for every non-System identity.
        await Mesh.GetEffectivePermissions(PackageInstaller.InstalledPartition, PlatformAdmin)
            .Should().Match(p => p == Permission.Read);
    }
}
