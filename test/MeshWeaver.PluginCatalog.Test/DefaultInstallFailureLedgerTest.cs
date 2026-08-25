#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// 🚨 <b>#2254 — a default install that SKIPPED a package must say so, and must try again.</b>
///
/// <para><b>What broke.</b> <c>InstanceAutoRegistrationService</c>'s <c>[DefaultInstall]</c> pass
/// sends a <c>CreateOrUpdateNodeRequest</c> per package to a per-instance NodeOps hub. When that
/// hub does not answer inside the 60 s request budget the service logs the timeout and steps over
/// the package — which is correct on its own ("one unreachable package must not withhold the
/// rest"). The defect was what happened NEXT: the pass's summary carried every package it TOUCHED
/// in one <c>Packages</c> list, failures included, and the seed ledger was written from that list.
/// So a failed package was recorded as SEEDED — and the seed lane skips a seeded package forever
/// ("the only way it can be gone is that someone removed it"). One transient timeout therefore
/// became a package this installation would never install again.</para>
///
/// <para>The class doc had promised the opposite all along — <i>"a package that FAILED stays off
/// the ledger and is retried next boot — that retry is the repair"</i> — so the repair the design
/// describes had simply never run. On memex-cloud, <c>MyAi</c> was still missing days later.</para>
///
/// <para><b>Both directions are asserted here</b>, because a ledger check that only looks for the
/// absence of the bad id passes just as well against a ledger nothing ever wrote: the GOOD package
/// must be ON the seeded list, the FAILED one must not, the failure must be NAMED durably, and the
/// next pass must re-attempt exactly the failed one and leave the delivered one alone.</para>
/// </summary>
public class DefaultInstallFailureLedgerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The name <c>InstanceAutoRegistrationService.Sources</c> gives a DI-registered source.</summary>
    private const string SourceName = "registered-0";

    private const string LedgerPath = PackageInstaller.InstalledPartition + "/_DefaultInstallLedger";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IPackageSource>(_ => Source())
                // The SEED lane, deliberately — not the pre-installed baseline. A `preInstalled`
                // package is RECONCILED and exempt from the ledger by design (it re-asserts every
                // boot), so it can never show this defect. Only a seed-once package can.
                .AddSingleton(new PluginCatalogOptions
                {
                    InstallByDefault = [$"{SourceName}/*"],
                    InstallPreInstalledPackages = false,
                }));

    private static readonly IReadOnlyList<RepoFile> Repo =
    [
        new("Good/index.json",
            """{"$type":"MeshNode","id":"Good","namespace":"","path":"Good","mainNode":"Good","name":"Good","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Installs fine.","minMeshVersion":"1.0.0"}}"""),
        new("Good/Page.json",
            """{"$type":"MeshNode","id":"Page","namespace":"Good","path":"Good/Page","mainNode":"Good/Page","name":"Page","nodeType":"Markdown","state":"Active","content":"# Good"}"""),
        new("Bad/index.json",
            """{"$type":"MeshNode","id":"Bad","namespace":"","path":"Bad","mainNode":"Bad","name":"Bad","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"Never lands.","minMeshVersion":"1.0.0"}}"""),
        new("Bad/Page.json",
            """{"$type":"MeshNode","id":"Page","namespace":"Bad","path":"Bad/Page","mainNode":"Bad/Page","name":"Page","nodeType":"Markdown","state":"Active","content":"# Bad"}"""),
    ];

    /// <summary>
    /// A catalog that LISTS both packages and fails to deliver one of them — with the production
    /// exception, verbatim: the per-instance NodeOps hub not answering inside the request budget.
    /// The package is perfectly well-formed; only its delivery fails, which is the whole point (a
    /// malformed package would be a permanent condition and SHOULD stay skipped).
    /// </summary>
    private sealed class HalfBrokenCatalog(NodeRepoPackageSource inner) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
            => inner.ListPackages(gitRef);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(
            PackageManifest package, string gitRef)
            => package.Id == "Bad"
                ? Observable.Throw<IReadOnlyList<PackageFile>>(new TimeoutException(
                    "No response received in hub portal/nodeops-jWxN3FpJZkO_SucRI-b6aQ within "
                    + "00:01:00 for request CreateOrUpdateNodeRequest (id=QVb5rTnxl0OgoGs7LXWuwQ) "
                    + "-> target <unset>. The request may have been undeliverable or the target "
                    + "hub was not found."))
                : inner.FetchPackageFiles(package, gitRef);
    }

    private static IPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-2254", Repo));
        return new HalfBrokenCatalog(
            new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins"));
    }

    [Fact(Timeout = 180_000)]
    public async Task AFailedPackage_IsNamed_StaysOffTheSeededLedger_AndIsRetriedOnTheNextPass()
    {
        // PASS 1 — the boot pass itself (the hosted service's own AsyncSubject; no polling).
        var first = await Installer().Completed
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

        Output.WriteLine($"pass 1: {first}");

        // The pass is not reported as clean.
        first.Failed.Should().Be(1, "exactly one package could not be delivered");
        // NAMED, not merely counted — the id is what the ledger needs to keep it off the seeded list.
        first.Failures.Should().HaveCount(1);
        first.Failures.Should().Contain("Bad");
        first.Delivered.Should().Contain("Good");
        first.Delivered.Should().NotContain("Bad",
            "'covered' and 'delivered' are different sets; conflating them is the defect");

        // THE LEDGER — both directions.
        var ledger = await ReadLedger();
        ledger.Should().NotBeNull("the ledger must actually have been written, or nothing below "
                                  + "discriminates anything");
        ledger!.Seeded.Should().Contain("Good",
            "a delivered package IS seeded — without this the assertion below would pass against "
            + "a ledger that recorded nothing at all");
        ledger.Seeded.Should().NotContain("Bad",
            "a FAILED install must never be recorded as seeded: the seed lane skips a seeded "
            + "package forever, so one transient NodeOps timeout would permanently un-install it");
        ledger.Failed.Should().Contain("Bad",
            "an unrecovered failure must leave a durable trace — otherwise a missing default "
            + "package is only findable by grepping a boot log");

        // PASS 2 — the repair. The delivered package is left alone; the failed one is re-attempted.
        var second = await Installer().RunDefaultInstall()
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).ToTask();

        Output.WriteLine($"pass 2: {second}");

        second.Packages.Should().Contain("Bad",
            "a package that failed must be RE-ATTEMPTED on the next pass — that retry is the "
            + "repair the whole ledger mechanism exists to allow");
        second.Packages.Should().NotContain("Good",
            "a package the seed has delivered is never re-asserted; if it were, this test would "
            + "prove nothing about the ledger being consulted at all");
    }

    private InstanceAutoRegistrationService Installer() =>
        Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

    /// <summary>Authoritative single-node read straight off storage (never the lagging index).</summary>
    private Task<DefaultInstallLedger?> ReadLedger() =>
        Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>()
            .Read(LedgerPath, Mesh.JsonSerializerOptions)
            .Select(n => n?.ContentAs<DefaultInstallLedger>(Mesh.JsonSerializerOptions))
            .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask();
}
