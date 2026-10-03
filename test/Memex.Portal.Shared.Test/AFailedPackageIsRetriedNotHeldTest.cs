using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
/// 🚨 The governed-provision hold (<see cref="InstanceAutoRegistrationService.HoldsForGovernedProvision"/>)
/// must not swallow the ledger's RETRY. A package whose install FAILED is kept off the seeded list
/// on purpose, so the next pass re-attempts it (#2254). The hold asked only "was it seeded?", so on
/// the second pass that package read as NEWLY LISTED and was held: the retry the ledger exists for
/// never ran, and a package the ledger had already classified as skipped (a paid tier, sales only)
/// stopped being re-classified. MeshWeaver.Plugins' <c>DefaultInstallFailureLedgerTest</c> and
/// <c>DefaultInstallCommercialSkipTest</c> went red on the first sealed set that carried the hold.
///
/// <para>A package the ledger has RECORDED — seeded, failed, or skipped with a standing reason — is
/// known to this instance, and the hold is for packages it has never seen.</para>
/// </summary>
public class AFailedPackageIsRetriedNotHeldTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string SourceName = "registered-0";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IPackageSource>(_ => Source())
                // The SEED lane under a whole-source pattern — exactly what the hold keys on.
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

    /// <summary>Lists both packages and fails to deliver <c>Bad</c> — a delivery failure, not a malformed package.</summary>
    private sealed class HalfBrokenCatalog(NodeRepoPackageSource inner) : IPackageSource
    {
        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef)
            => inner.ListPackages(gitRef);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef)
            => package.Id == "Bad"
                ? Observable.Throw<IReadOnlyList<PackageFile>>(new TimeoutException("NodeOps did not answer"))
                : inner.FetchPackageFiles(package, gitRef);
    }

    private static IPackageSource Source()
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-hold", Repo));
        return new HalfBrokenCatalog(new NodeRepoPackageSource(fetch, "https://github.com/acme/plugins"));
    }

    [Fact(Timeout = 180_000)]
    public async Task APackageThatFailed_IsReattemptedOnTheNextPass_NotHeldAsNewlyListed()
    {
        var installer = Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>();

        // PASS 1 — a FRESH instance, so the hold does not apply: Good lands, Bad fails.
        var first = await installer.Completed
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"pass 1: {first}");
        first.Failures.Should().Contain("Bad");
        first.Delivered.Should().Contain("Good",
            "without a seeded package the second pass would still be FRESH and the hold could not fire");

        // PASS 2 — no longer fresh. Bad is covered only by the wildcard and was never seeded, which
        // is the hold's exact shape; the ledger's FAILED record is what makes it known.
        var second = await installer.RunDefaultInstall()
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"pass 2: {second}");
        second.Packages.Should().Contain("Bad",
            "a package the ledger recorded as FAILED is known to this instance and must be re-attempted, "
            + "not held as newly listed");
        second.Packages.Should().NotContain("Good", "a seeded package is still left alone");
    }

    /// <summary>The set the hold consults: seeded, failed and skipped are all KNOWN. Pure.</summary>
    [Fact]
    public void TheLedgersFailedAndSkippedEntries_AreKnown()
    {
        var ledger = new DefaultInstallLedger
        {
            Seeded = ["Good"],
            Failed = ["Bad"],
            Skipped = [new DefaultInstallSkip("Paid", "tier not purchasable unattended")],
        };

        var known = InstanceAutoRegistrationService.KnownToTheLedger(ledger);

        known.Should().Contain("Good");
        known.Should().Contain("Bad");
        known.Should().Contain("Paid");
        known.Should().NotContain("Fresh", "a package the ledger never recorded is the one the hold is for");
    }

    /// <summary>
    /// A persisted ledger can carry an explicit <c>null</c> for any of its lists (an older
    /// record, or a writer that serialised a null). The hold runs inside the boot install, so a
    /// null list must read as empty — never throw and abort the pass. Pure.
    /// </summary>
    [Fact]
    public void ALedgerWithNullLists_ReadsAsEmpty()
    {
        var ledger = System.Text.Json.JsonSerializer.Deserialize<DefaultInstallLedger>(
            """{"seeded":null,"failed":null,"skipped":null}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("the ledger JSON deserialised to null");
        ledger.Failed.Should().BeNull("the precondition: the record really carries a null list");

        var known = InstanceAutoRegistrationService.KnownToTheLedger(ledger);

        known.Should().BeEmpty("a null list is an empty list, not a fault in the boot install");
    }
}
