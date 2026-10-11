using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A commercial package the DEPLOYMENT CONFIGURATION names exactly is INSTALLED at boot</b>
/// — policy <c>deployment-config-authorizes-named-commercial</c>.
///
/// <para>The deployment record (<c>PluginCatalog:InstallByDefault</c>) changes only by a reviewed,
/// App-merged pull request, so it is the authorizing principal —
/// <c>deployment-config:{instance}</c> (<see cref="DeploymentConfigAuthority"/>) — for a package it
/// names EXACTLY and for what that package requires (commercial requirements included). A
/// whole-source pattern names nothing: a commercial package it merely covers is still skipped.
/// The ledger still seeds once, and records the principal under <c>authorized</c>.</para>
/// </summary>
public class ADeploymentNamedCommercialPackageInstallsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SourceName = "registered-0";
    private const string Instance = "partnerre-test";
    private const string Principal = PackageEntitlement.DeploymentConfigPrefix + Instance;
    private const string LedgerPath = PackageInstaller.InstalledPartition + "/_DefaultInstallLedger";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IPackageSource>(_ => new NodeRepoPackageSource(
                    (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-deployment-named", Repo)),
                    "https://github.com/acme/plugins"))
                // The PartnerRe shape: the whole source as a seed AND one commercial module named
                // exactly — the named one is the decision, the wildcard is not.
                .AddSingleton(new PluginCatalogOptions
                {
                    InstanceId = Instance,
                    InstallByDefault = [$"{SourceName}/*", $"{SourceName}/Claims"],
                    InstallPreInstalledPackages = false,
                }));

    private static RepoFile Index(string id, string extraContent) => new(
        $"{id}/index.json",
        $$"""{"$type":"MeshNode","id":"{{id}}","namespace":"","path":"{{id}}","mainNode":"{{id}}","name":"{{id}}","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"{{id}}.","minMeshVersion":"1.0.0"{{extraContent}} } }""");

    private static RepoFile Page(string id) => new(
        $"{id}/Page.json",
        $$"""{"$type":"MeshNode","id":"Page","namespace":"{{id}}","path":"{{id}}/Page","mainNode":"{{id}}/Page","name":"Page","nodeType":"Markdown","state":"Active","content":"# {{id}}"}""");

    /// <summary>
    /// Free installs anyway; Claims (priced) is NAMED and requires ClaimsCore (contact-sales, so
    /// commercial without a price); Other (priced) is covered only by the wildcard.
    /// </summary>
    private static readonly ImmutableList<RepoFile> Repo =
    [
        Index("Free", ""), Page("Free"),
        Index("Claims", ",\"price\":900,\"currency\":\"CHF\",\"requires\":[\"ClaimsCore\"]"), Page("Claims"),
        Index("ClaimsCore", ",\"contactEmail\":\"sales@acme.test\""), Page("ClaimsCore"),
        Index("Other", ",\"price\":490,\"currency\":\"CHF\""), Page("Other"),
    ];

    [Fact(Timeout = 240_000)]
    public async Task ANamedCommercialPackage_AndItsCommercialRequirement_InstallAtBoot_AWildcardOnlyOneIsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var installer = Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

        // PASS 1 — the first boot of a fresh instance.
        var first = await installer.Completed
            .FirstAsync().Timeout(TimeSpan.FromSeconds(180)).Await(ct);
        Output.WriteLine($"pass 1: {first}");

        first.Failed.Should().Be(0);
        first.Delivered.Should().Contain("Free");
        first.Delivered.Should().Contain("Claims",
            "a commercial package the deployment configuration names exactly is authorized by it");
        first.Delivered.Should().Contain("ClaimsCore",
            "a commercial REQUIREMENT of a named package is authorized with it — the named module "
            + "does not work without it");
        first.Skipped.Select(s => s.Package).Should().Equal(["Other"],
            "a whole-source pattern names nothing: a commercial package it merely covers stays skipped");
        first.Skipped.Single().Reason.Should().Contain("InstallByDefault",
            "the skip reason names the remedy — naming the package exactly");
        first.Authorized.Select(a => a.Package).OrderBy(x => x).Should().Equal("Claims", "ClaimsCore");

        // THE INSTALL RECORD names the principal — the unattended update re-verifies this one.
        var record = await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read($"{PackageInstaller.InstalledPartition}/Claims", Mesh.JsonSerializerOptions)
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);
        record.Should().NotBeNull("the named commercial package landed with its record");
        record!.ContentAs<PackageManifest>(Mesh.JsonSerializerOptions)!.AuthorizedBy
            .Should().Be(Principal, "the record says WHO authorized the install: the deployment configuration");

        // INSTALLING GRANTS NOBODY ACCESS — a commercial partition lands gated, no public read.
        (await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
                .Read(PackageInstaller.CoverGrantPath("Claims"), Mesh.JsonSerializerOptions)
                .Take(1).Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().BeNull("who reads a commercial package stays the plan's and the subscriptions' decision");

        // THE LEDGER records it — principal and reason — beside the seeded list.
        var ledger = await ReadLedger().WaitAsync(ct);
        ledger.Should().NotBeNull();
        ledger!.Seeded.Should().Contain("Claims").And.Contain("ClaimsCore").And.Contain("Free");
        ledger.Skipped.Select(s => s.Package).Should().Equal(["Other"]);
        var claims = ledger.Authorized.Single(a => a.Package == "Claims");
        claims.InstalledBy.Should().Be(Principal);
        claims.Reason.Should().Contain($"'{SourceName}/Claims'", "the reason quotes the entry that named it");
        ledger.Authorized.Single(a => a.Package == "ClaimsCore").Reason.Should().Contain("required by 'Claims'");

        // THE PACKAGE IS GONE — the seed must not bring it back. The record is removed straight
        // from storage ON PURPOSE: the uninstall lifecycle (PackageUninstallExecutor) also writes an
        // "uninstalled here" marker that blocks every unattended lane by itself, which would make
        // this leg pass without the LEDGER — the thing under test — doing anything. Nothing below
        // reads the record through a hub or a cache: the seeded package is dropped before any read.
        await Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Delete($"{PackageInstaller.InstalledPartition}/Claims")
            .Take(1).Timeout(TestTimeouts.Convergence).Await(ct);

        var second = await installer.RunDefaultInstall()
            .FirstAsync().Timeout(TimeSpan.FromSeconds(180)).Await(ct);
        Output.WriteLine($"pass 2: {second}");
        second.Packages.Should().NotContain("Claims",
            "the ledger records Claims as seeded: a named commercial package is seeded ONCE, and an "
            + "operator's removal is not undone by the next restart");
        second.Skipped.Select(s => s.Package).Should().Equal(["Other"],
            "the wildcard-only commercial package is still re-classified, not installed");

        var after = await ReadLedger().WaitAsync(ct);
        after!.Authorized.Single(a => a.Package == "Claims").InstalledAt.Should().Be(claims.InstalledAt,
            "the authorization trail is cumulative — the original decision stays on record");
    }

    /// <summary>The gate verifies the principal against the CURRENT configuration — never its string.</summary>
    [Fact(Timeout = 120_000)]
    public async Task TheGate_AdmitsOnlyWhatTheConfigurationNames_OnThisInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var claims = new PackageManifest { Id = "Claims", Source = SourceName, Price = 900m, Currency = "CHF" };
        var other = new PackageManifest { Id = "Other", Source = SourceName, Price = 490m, Currency = "CHF" };

        await PackageEntitlement.Authorize(Mesh, claims, Principal)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        var wildcardOnly = async () => await PackageEntitlement.Authorize(Mesh, other, Principal)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        (await wildcardOnly.Should().ThrowAsync<PackageAuthorizationException>())
            .Which.Message.Should().Contain("whole-source pattern");

        var anotherInstance = async () => await PackageEntitlement
            .Authorize(Mesh, claims, PackageEntitlement.DeploymentConfigPrefix + "someone-else")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);
        (await anotherInstance.Should().ThrowAsync<PackageAuthorizationException>())
            .Which.Message.Should().Contain("another instance");
    }

    [Fact]
    public void ThePrincipal_IsRecognisedByShapeOnly()
    {
        PackageEntitlement.IsDeploymentConfigPrincipal(Principal).Should().BeTrue();
        PackageEntitlement.IsDeploymentConfigPrincipal(" " + Principal + " ").Should().BeTrue();
        PackageEntitlement.IsDeploymentConfigPrincipal(PackageEntitlement.DeploymentConfigPrefix).Should().BeFalse();
        PackageEntitlement.IsDeploymentConfigPrincipal("rbuergi").Should().BeFalse();
        PackageEntitlement.IsDeploymentConfigPrincipal("Governance/Activities/x").Should().BeFalse();
        PackageEntitlement.IsGovernedActivityPrincipal(Principal).Should().BeFalse();
        PackageEntitlement.DeploymentConfigPrincipal(" partnerre ").Should().Be("deployment-config:partnerre");
    }

    [Fact]
    public void OnlyAnExactEntry_NamesAPackage()
    {
        var claims = new PackageManifest { Id = "Claims", Source = "PartnerRe" };
        DeploymentConfigAuthority.NamingEntry(["PartnerRe/*"], claims).Should().BeNull(
            "a whole-source pattern names nothing");
        DeploymentConfigAuthority.NamingEntry(["Plugins/Claims"], claims).Should().BeNull(
            "another source's package of the same id is not this one");
        DeploymentConfigAuthority.NamingEntry(["PartnerRe/*", "PartnerRe/Claims"], claims)
            .Should().Be("PartnerRe/Claims");
        DeploymentConfigAuthority.NamingEntry(null, claims).Should().BeNull();
    }

    [Fact]
    public void TheClassifier_LetsAnAuthorizedCommercialPackageThrough_ButNotAPlanRefusal()
    {
        var paid = new PackageManifest { Id = "P", Price = 490m, Currency = "CHF" };
        InstanceAutoRegistrationService.TerminalSkipReason(paid, configAuthorized: true).Should().BeNull(
            "the deployment configuration authorizes it");
        InstanceAutoRegistrationService.TerminalSkipReason(paid, configAuthorized: false)
            .Should().Contain("Global Admin").And.Contain("InstallByDefault");
        InstanceAutoRegistrationService.TerminalSkipReason(paid).Should().NotBeNull(
            "the one-argument form keeps the unauthorized meaning");
    }

    [Fact]
    public void TheRequirementClosure_FollowsCommercialEdges_FromNamedRootsOnly()
    {
        PackageManifest M(string id, params string[] requires) => new()
        {
            Id = id, Source = SourceName, Requires = requires.ToImmutableList(),
        };
        ImmutableList<PackageManifest> catalog =
        [
            M("Claims", "ClaimsCore@^1.0.0"),
            M("ClaimsCore", "Pricing") with { ContactEmail = "sales@acme.test" },
            M("Pricing") with { Price = 10m },
            M("Course", "PaidCourse"),
            M("PaidCourse") with { Price = 49m },
        ];

        var closure = InstanceAutoRegistrationService.ConfigAuthorizedRequirements(catalog, [catalog[0]]);

        closure.Keys.OrderBy(x => x).Should().Equal("ClaimsCore", "Pricing");
        closure["Pricing"].Should().Be("Claims", "the transitive requirement is traced to the named root");

        // The closure decides what DependencyClosure may pull in: Claims' commercial chain, never
        // the commercial requirement of a package nothing named.
        var authorized = closure.Keys.Append("Claims").ToImmutableHashSet(StringComparer.Ordinal);
        var withDependencies = InstanceAutoRegistrationService.DependencyClosure(
            catalog, [catalog[0], catalog[3]], null, authorized);
        withDependencies.Select(p => p.Id).Should().Equal("Claims", "ClaimsCore", "Course", "Pricing");
    }

    [Fact]
    public void TheAuthorizationTrail_KeepsTheOriginalDecision_AndRecordsAChangedOne()
    {
        var at = DateTimeOffset.Parse("2026-10-11T08:00:00Z");
        var original = new DefaultInstallAuthorization("Claims", Principal, "named", at);
        var reasserted = original with { InstalledAt = at.AddDays(1) };
        var other = new DefaultInstallAuthorization("Pricing", Principal, "required by 'Claims'", at.AddDays(1));

        InstanceAutoRegistrationService.MergeAuthorizations([original], [reasserted, other])
            .Should().Equal(original, other);
        InstanceAutoRegistrationService.MergeAuthorizations(null, [reasserted with { Reason = "changed" }])
            .Single().Reason.Should().Be("changed");
    }

    private Task<DefaultInstallLedger?> ReadLedger() =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(LedgerPath, Mesh.JsonSerializerOptions)
            .Select(n => n?.ContentAs<DefaultInstallLedger>(Mesh.JsonSerializerOptions))
            .Take(1).Timeout(TestTimeouts.Convergence).Await();
}
