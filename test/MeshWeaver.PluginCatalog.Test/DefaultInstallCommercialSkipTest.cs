#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// 🚨 <b>#2536 — an authorization refusal is TERMINAL for an unattended installer, and must be a
/// classification, not a retried failure.</b>
///
/// <para><b>What broke.</b> The default-install set on memex-cloud listed commercial packages
/// (Claims, SST, Underwriting, …). <see cref="PackageEntitlement.Authorize"/> refused each one —
/// correctly: the boot has no authorizing principal and no human can appear at boot to pay — but
/// the sweep recorded every refusal as FAILED on <c>Plugins/_DefaultInstallLedger</c> and re-attempted
/// it at the next boot, logging "that retry is the repair". For an authorization refusal that is
/// false: every boot re-failed identically, at Error, with a stack trace, forever.</para>
///
/// <para><b>The fix under test.</b> A commercial package is classified BEFORE any attempt
/// (<see cref="InstanceAutoRegistrationService.TerminalSkipReason"/>) and recorded as a terminal
/// SKIP — named, with its reason, on the ledger — never as a failure. Nothing retries it; what
/// changes the outcome is an EVENT the next pass observes: a Global Admin installs it from the
/// catalog, or the package's own terms change. The last leg here IS that event — the manifest
/// stops being commercial, and the very next pass installs the package with no ceremony.</para>
///
/// <para>Both directions are asserted, as in <see cref="DefaultInstallFailureLedgerTest"/>: the
/// free package must be delivered and seeded (so the skip assertions cannot pass against a pass
/// that did nothing), and the commercial ones must be skipped-with-reason, kept off both the
/// seeded and the failed lists, and left un-attempted by later passes.</para>
/// </summary>
public class DefaultInstallCommercialSkipTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The name <c>InstanceAutoRegistrationService.Sources</c> gives a DI-registered source.</summary>
    private const string SourceName = "registered-0";

    private const string LedgerPath = PackageInstaller.InstalledPartition + "/_DefaultInstallLedger";

    /// <summary>The catalog as the source serves it NOW — mutable so the entitlement-change leg
    /// can flip a package's terms mid-test, exactly as a republished manifest would.</summary>
    private IReadOnlyList<RepoFile> repo = CommercialRepo;

    private string commit = "commit-2536-commercial";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IPackageSource>(_ => new NodeRepoPackageSource(
                    (_, _, _, _) => Observable.Return(new RepoSnapshot(commit, repo)),
                    "https://github.com/acme/plugins"))
                // The seed lane with a wildcard — the memex-cloud shape (`Plugins/*` sweeping in
                // commercial packages). The pre-installed baseline is off so every selection here
                // comes from the operator's pattern.
                .AddSingleton(new PluginCatalogOptions
                {
                    InstallByDefault = [$"{SourceName}/*"],
                    InstallPreInstalledPackages = false,
                }));

    private static RepoFile Index(string id, string extraContent) => new(
        $"{id}/index.json",
        $$"""{"$type":"MeshNode","id":"{{id}}","namespace":"","path":"{{id}}","mainNode":"{{id}}","name":"{{id}}","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"{{id}}.","minMeshVersion":"1.0.0"{{extraContent}} } }""");

    private static RepoFile Page(string id) => new(
        $"{id}/Page.json",
        $$"""{"$type":"MeshNode","id":"Page","namespace":"{{id}}","path":"{{id}}/Page","mainNode":"{{id}}/Page","name":"Page","nodeType":"Markdown","state":"Active","content":"# {{id}}"}""");

    private const string PaidTerms = ",\"price\":490,\"currency\":\"CHF\"";
    private const string SalesTerms = ",\"contactEmail\":\"sales@acme.test\"";

    /// <summary>Free installs; Paid (a price) and SalesOnly (a sales contact, no price — the
    /// Claims/SST shape) are commercial in the two ways <see cref="PackageEntitlement.IsCommercial"/>
    /// recognises.</summary>
    private static readonly IReadOnlyList<RepoFile> CommercialRepo =
    [
        Index("Free", ""), Page("Free"),
        Index("Paid", PaidTerms), Page("Paid"),
        Index("SalesOnly", SalesTerms), Page("SalesOnly"),
    ];

    /// <summary>The same catalog after the vendor changes Paid's terms — the entitlement-change
    /// EVENT: no price any more, so nothing is left to refuse.</summary>
    private static readonly IReadOnlyList<RepoFile> FreedRepo =
    [
        Index("Free", ""), Page("Free"),
        Index("Paid", ""), Page("Paid"),
        Index("SalesOnly", SalesTerms), Page("SalesOnly"),
    ];

    [Fact]
    public void TheClassifierIsPure_AndCountsBothCommercialShapes()
    {
        // Priced — positive (purchasable) and negative (coupon-only, the UWDeepfield shape).
        InstanceAutoRegistrationService.TerminalSkipReason(
                new PackageManifest { Id = "P", Price = 490m, Currency = "CHF" })
            .Should().Contain("Global Admin").And.Contain("price 490");
        InstanceAutoRegistrationService.TerminalSkipReason(
                new PackageManifest { Id = "C", Price = -1m })
            .Should().NotBeNull("a negative price means coupon-only, which is still commercial");
        // Contact-sales — commercial WITHOUT a price (the Claims/SST shape).
        InstanceAutoRegistrationService.TerminalSkipReason(
                new PackageManifest { Id = "S", ContactEmail = "sales@acme.test" })
            .Should().Contain("contact sales");
        // Free — no price (or an explicit 0) and nobody to ask: nothing to skip.
        InstanceAutoRegistrationService.TerminalSkipReason(new PackageManifest { Id = "F" })
            .Should().BeNull();
        InstanceAutoRegistrationService.TerminalSkipReason(
                new PackageManifest { Id = "Z", Price = 0m })
            .Should().BeNull("price 0 is explicitly free");
    }

    [Fact(Timeout = 180_000)]
    public async Task ACommercialPackage_IsSkippedWithReason_NeverRetried_AndInstallsWhenItsTermsChange()
    {
        // PASS 1 — the boot pass (the hosted service's own AsyncSubject; no polling).
        var first = await Installer().Completed
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await();

        Output.WriteLine($"pass 1: {first}");

        // The refusal is a CLASSIFICATION: no failure is counted and nothing was attempted.
        first.Failed.Should().Be(0,
            "an authorization refusal is terminal for an unattended installer — reporting it as a "
            + "failure is what made every boot re-fail identically (#2536)");
        first.Delivered.Should().Contain("Free",
            "the free package must actually install, or the skip assertions below prove nothing");
        first.Packages.Should().NotContain("Paid").And.NotContain("SalesOnly",
            "a terminally-skipped package is never ATTEMPTED — no install, no exception, no fail: line");
        first.Skipped.Select(s => s.Package).OrderBy(x => x).Should().Equal("Paid", "SalesOnly");
        first.Skipped.Should().OnlyContain(s => s.Reason.Contains("Global Admin"),
            "the skip must carry the actionable reason — who can change the situation and how");

        // THE LEDGER — the durable, diagnosable record.
        var ledger = await ReadLedger();
        ledger.Should().NotBeNull("the ledger must actually have been written");
        ledger!.Seeded.Should().Contain("Free");
        ledger.Seeded.Should().NotContain("Paid",
            "a skipped package was never delivered, so seeding it would block the install that "
            + "becomes possible when its terms change");
        ledger.Failed.Should().BeEmpty(
            "FAILED is for attempts a retry can repair; an authorization refusal is not one");
        ledger.Skipped.Select(s => s.Package).OrderBy(x => x).Should().Equal("Paid", "SalesOnly");
        ledger.Skipped.Should().OnlyContain(s => !string.IsNullOrWhiteSpace(s.Reason));

        // PASS 2 — no retry. The skip is re-DERIVED (the manifest is still commercial), not re-attempted.
        var second = await Installer().RunDefaultInstall()
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await();

        Output.WriteLine($"pass 2: {second}");

        second.Packages.Should().BeEmpty(
            "Free is seeded and the commercial two are skipped — a second boot attempts NOTHING, "
            + "which is exactly the per-boot re-fail this change removes");
        second.Failed.Should().Be(0);
        second.Skipped.Select(s => s.Package).OrderBy(x => x).Should().Equal(
            ["Paid", "SalesOnly"],
            "the standing decision is re-derived each pass and stays visible on the ledger");

        // THE EVENT — Paid's terms change: the vendor drops the price. This, not a retry, is what
        // changes the outcome; the next pass sees the new manifest and simply installs.
        repo = FreedRepo;
        commit = "commit-2536-freed";

        var third = await Installer().RunDefaultInstall()
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await();

        Output.WriteLine($"pass 3: {third}");

        third.Delivered.Should().Contain("Paid",
            "a package that stops being commercial installs on the very next pass — the skip is a "
            + "classification of the CURRENT manifest, never a permanent ban");
        third.Failed.Should().Be(0);
        third.Skipped.Select(s => s.Package).Should().Equal(
            ["SalesOnly"],
            "the snapshot drops what is no longer refusable and keeps what still is");

        var after = await ReadLedger();
        after!.Seeded.Should().Contain("Paid").And.Contain("Free");
        after.Skipped.Select(s => s.Package).Should().Equal(["SalesOnly"]);
        after.Failed.Should().BeEmpty();
    }

    private InstanceAutoRegistrationService Installer() =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<DefaultInstallLedger?> ReadLedger() =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(LedgerPath, Mesh.JsonSerializerOptions)
            .Select(n => n?.ContentAs<DefaultInstallLedger>(Mesh.JsonSerializerOptions))
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).Await();
}
