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
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// THE FREE / COMMERCIAL BOUNDARY (#830). "The other plugins need special permission of Global
/// Admin to be able to access COMMERCIAL packages ⇒ only FREE ones can be synced without
/// permission."
///
/// <list type="bullet">
///   <item>Free (<c>Price</c> null or 0) — installs and auto-updates for anyone, no special
///     permission.</item>
///   <item>Commercial (a non-zero <c>Price</c>) — requires Global Admin on the installing instance;
///     refused for a plain signed-in principal AND for the unattended paths that carry no principal
///     at all.</item>
/// </list>
///
/// <para>The point of the fixture is WHERE the check lives. Before this, the catalog SURFACE was
/// admin-only while the machine paths — the boot default install and
/// <see cref="PluginUpdateWatcher"/>'s auto-update — installed priced packages with no check
/// whatsoever. So every assertion here goes through the ACTION
/// (<see cref="PackageInstaller.Install"/> / <see cref="CatalogLayoutAreas.InstallOrUpdate"/>,
/// which is the exact call the watcher makes), never through a rendered control.</para>
///
/// <para>Built on <see cref="MonolithMeshTestBase.ConfigureMeshBase"/>: the default fixture seeds a
/// root-scope <c>Public → Admin</c> grant that would make the "plain user" a global admin and void
/// every refusal assertion.</para>
/// </summary>
public class CommercialPackageAuthorizationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The production-shaped platform admin: Admin on the Admin partition, nothing else.</summary>
    private const string PlatformAdmin = "commercial-admin";

    /// <summary>A signed-in principal holding NOTHING — no role, no grant, anywhere.</summary>
    private const string PlainUser = "plain-user";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddPluginCatalog()
            .AddMeshNodes(AssignmentNodeFactory.UserRole(PlatformAdmin, "Admin", "Admin"));

    private static PackageManifest Free(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = PackageKind.Content,
        TargetPartition = id,
        SourceFolder = id,
        Version = "1.0.0",
    };

    private static PackageManifest Commercial(string id) => Free(id) with
    {
        Price = 900m,
        Currency = "CHF",
    };

    /// <summary>
    /// Sold with a person in the loop: NO price — "nothing to self-serve" — and a sales contact.
    /// Commercial all the same, which is the half the price-only test used to miss.
    /// </summary>
    private static PackageManifest ContactSales(string id) => Free(id) with
    {
        ContactEmail = "info@systemorph.com",
    };

    /// <summary>A source serving one package's single file — the shape both install paths consume.</summary>
    private sealed class SingleFileSource(PackageManifest manifest) : IPackageSource
    {
        public int Fetches { get; private set; }

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>([manifest]);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
        {
            Fetches++;
            return Observable.Return<IReadOnlyList<PackageFile>>(
                [new PackageFile($"{package.Id}/Doc.md", $"# {package.Id}")]);
        }
    }

    private Task<InstallResult> Install(PackageManifest manifest, string? authorizingUserId) =>
        PackageInstaller.Install(
                Mesh, manifest, [new PackageFile($"{manifest.Id}/Doc.md", $"# {manifest.Id}")], "HEAD",
                authorizingUserId: authorizingUserId)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<MeshNode?> Read(string path) =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(path, Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();

    [Fact(Timeout = 180_000)]
    public async Task FreePackage_InstallsForANonAdmin_AndUnattended()
    {
        // The signed-in principal holds nothing anywhere …
        await Mesh.IsGlobalAdmin(PlainUser).Should().Match(isAdmin => !isAdmin);

        var byUser = await Install(Free("free-by-user"), PlainUser);
        byUser.Written.Should().BeGreaterThan(0, "a free package needs no special permission");
        (await Read($"{PackageInstaller.InstalledPartition}/free-by-user")).Should().NotBeNull();

        // … and the unattended path (boot provisioning, the update watcher) has no principal at
        // all, which must not stop a free package either — that is what "syncable without
        // permission" means.
        var unattended = await Install(Free("free-unattended"), authorizingUserId: null);
        unattended.Written.Should().BeGreaterThan(0);
    }

    [Fact(Timeout = 180_000)]
    public async Task CommercialPackage_IsRefusedForANonAdmin_AndForAnUnattendedInstall()
    {
        var refusedForUser = await Record.ExceptionAsync(() => Install(Commercial("paid-by-user"), PlainUser));
        refusedForUser.Should().BeOfType<PackageAuthorizationException>(
            "a priced package requires Global Admin to be installed at all");
        refusedForUser!.Message.Should().Contain("Global Admin",
            "the refusal must carry a speaking reason, never be a silent skip");
        refusedForUser.Message.Should().Contain("paid-by-user", "the reason must name the package");

        (await Read($"{PackageInstaller.InstalledPartition}/paid-by-user"))
            .Should().BeNull("a refused install must write nothing — not even the record");
        (await Read("paid-by-user/Doc"))
            .Should().BeNull("a refused install must not land content either");

        // The machine path: nobody authorized it, so a priced package cannot ride in on the
        // unattended default install.
        var refusedUnattended = await Record.ExceptionAsync(
            () => Install(Commercial("paid-unattended"), authorizingUserId: null));
        refusedUnattended.Should().BeOfType<PackageAuthorizationException>(
            "an unattended install has no principal — for a priced package that fails closed");
        (await Read($"{PackageInstaller.InstalledPartition}/paid-unattended")).Should().BeNull();
    }

    /// <summary>
    /// A CONTACT-SALES package is commercial without carrying a price, so the same gate applies:
    /// refused for a plain principal and for the unattended paths. Before this, such a package read
    /// as free and any instance that could see the catalog auto-installed it — the opposite of what
    /// "call us before you use this" means. The refusal names the contact instead of an absent
    /// price, so the log line does not read as a broken gate.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task ContactSalesPackage_IsRefusedForANonAdmin_AndForAnUnattendedInstall()
    {
        var refusedForUser = await Record.ExceptionAsync(
            () => Install(ContactSales("contact-by-user"), PlainUser));
        refusedForUser.Should().BeOfType<PackageAuthorizationException>(
            "a contact-sales package requires Global Admin to be installed at all");
        refusedForUser!.Message.Should().Contain("contact sales: info@systemorph.com",
            "the refusal must name what made it commercial — there is no price to print");
        refusedForUser.Message.Should().Contain("contact-by-user", "the reason must name the package");

        (await Read($"{PackageInstaller.InstalledPartition}/contact-by-user"))
            .Should().BeNull("a refused install must write nothing — not even the record");

        var refusedUnattended = await Record.ExceptionAsync(
            () => Install(ContactSales("contact-unattended"), authorizingUserId: null));
        refusedUnattended.Should().BeOfType<PackageAuthorizationException>(
            "an unattended install has no principal — for a contact-sales package that fails closed");
        (await Read($"{PackageInstaller.InstalledPartition}/contact-unattended")).Should().BeNull();
    }

    [Fact(Timeout = 180_000)]
    public async Task CommercialPackage_InstallsForAGlobalAdmin_AndStampsTheAuthorizer()
    {
        await Mesh.IsGlobalAdmin(PlatformAdmin).Should().Match(isAdmin => isAdmin);

        var result = await Install(Commercial("paid-by-admin"), PlatformAdmin);
        result.Written.Should().BeGreaterThan(0, "a global admin may install a commercial package");

        var record = await Read($"{PackageInstaller.InstalledPartition}/paid-by-admin");
        record.Should().NotBeNull();
        record!.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)!.AuthorizedBy
            .Should().Be(PlatformAdmin,
                "the record must remember WHO authorized it — that is what an unattended update of "
                + "a commercial package is re-checked against");
    }

    /// <summary>
    /// The auto-update path, at the exact call the watcher makes:
    /// <c>InstallOrUpdate(..., record.AuthorizedBy)</c>. A free package updates itself with no
    /// principal; a commercial one updates only when its record names a principal who is STILL a
    /// global admin — so revoking the admin stops the syncing, and a record that was never
    /// admin-authorized is refused rather than silently updated.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task AutoUpdate_AppliesToFree_AndIsRefusedForACommercialRecordWithoutAnAdminAuthorizer()
    {
        var free = Free("auto-free");
        var freeSource = new SingleFileSource(free);
        var freeResult = await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, freeSource, "HEAD", free, null, authorizingUserId: null)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();
        freeResult.Written.Should().BeGreaterThan(0, "a free package auto-updates unattended");

        // A commercial package whose record names nobody (the pre-#830 shape, and the shape a
        // package that only just acquired a price has) is refused — with a reason.
        var paid = Commercial("auto-paid");
        var paidSource = new SingleFileSource(paid);
        var refused = await Record.ExceptionAsync(() => CatalogLayoutAreas
            .InstallOrUpdate(Mesh, paidSource, "HEAD", paid, null, authorizingUserId: null)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask());
        refused.Should().BeOfType<PackageAuthorizationException>();
        paidSource.Fetches.Should().Be(0,
            "the refusal must precede the work — not one file may travel for a package that may "
            + "not be installed");

        // The same call, authorized by the record's admin, goes through.
        var applied = await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, paidSource, "HEAD", paid, null, authorizingUserId: PlatformAdmin)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();
        applied.Written.Should().BeGreaterThan(0);

        // And a principal who is not (or is no longer) an admin cannot keep it updating.
        var revoked = await Record.ExceptionAsync(() => CatalogLayoutAreas
            .InstallOrUpdate(Mesh, paidSource, "HEAD", paid, null, authorizingUserId: PlainUser)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask());
        revoked.Should().BeOfType<PackageAuthorizationException>(
            "the authorizer is re-verified on every update, not trusted from the record alone");
    }

    /// <summary>The pure rules, pinned on their own so a regression names itself.</summary>
    [Fact(Timeout = 30_000)]
    public void FreeAndCommercialAreDecidedByPriceAlone_AndTheAuthorizerCarriesForward()
    {
        new PackageManifest { Id = "x" }.IsCommercial().Should().BeFalse("no price = free");
        new PackageManifest { Id = "x", Price = 0m }.IsCommercial().Should().BeFalse("price 0 = free");
        new PackageManifest { Id = "x", Price = 900m }.IsCommercial().Should().BeTrue();
        // A negative price is the Store's coupon-only shape — not free either, and the same
        // "priced" test the installer already applies when it leaves a partition gated.
        new PackageManifest { Id = "x", Price = -1m }.IsCommercial().Should().BeTrue(
            "coupon-only is not free");

        // The re-stamp starts from the policy-less CATALOG manifest, so without the carry-forward
        // the first unattended update would erase the very authorization it is checked against.
        var record = new PackageManifest { Id = "x", AuthorizedBy = PlatformAdmin };
        PackageInstaller.SeedAuthorizedBy(record, null).Should().Be(PlatformAdmin);
        PackageInstaller.SeedAuthorizedBy(record, PlainUser).Should().Be(PlainUser,
            "an explicit authorizer for THIS action wins");
        PackageInstaller.SeedAuthorizedBy(null, null).Should().BeNull();
    }
}
