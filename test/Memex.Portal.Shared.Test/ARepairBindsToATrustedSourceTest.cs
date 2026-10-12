using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>The repair lane re-asserts an installed package only from a source TRUSTED to be the one
/// its record names — never from a repository that merely AUTHORS that name</b> (MeshWeaver#6512).
///
/// <para>Two directly read sources list the same id. The honest one (configured as
/// <c>registered-1</c>) served the install, so the record names <c>registered-1</c>. The impostor
/// (configured as <c>registered-0</c>, listed FIRST) authors <c>"source": "registered-1"</c> in its
/// manifest and serves different files at the same module version. Before #6512 the lane preferred
/// the first listing whose <c>Source</c> equals the record's — the impostor — and its files landed
/// over the installed package.</para>
///
/// <para><b>SHOULD-FAIL-IF</b> the lane trusts an authored source again: the lost node comes back
/// with the impostor's body.</para>
/// </summary>
public class ARepairBindsToATrustedSourceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string HonestSource = "registered-1";
    private const string ImpostorMarker = "IMPOSTOR body";

    private readonly ImpostorSource impostor = new();

    /// <summary>A direct source whose manifest AUTHORS the honest source's name — what a package.json
    /// may carry — and serves its own files. Lists nothing until the install has landed, so the
    /// install itself is unambiguous and only the repair meets the impostor.</summary>
    private sealed class ImpostorSource : IPackageSource
    {
        private volatile bool listing;

        public void StartListing() => listing = true;

        public IObservable<IReadOnlyList<PackageManifest>> ListPackages(string gitRef) =>
            Observable.Return<IReadOnlyList<PackageManifest>>(listing
                ? [ReassertFixture.Candidate() with { Source = HonestSource }]
                : []);

        public IObservable<IReadOnlyList<PackageFile>> FetchPackageFiles(PackageManifest package, string gitRef) =>
            Observable.Return<IReadOnlyList<PackageFile>>(ReassertFixture.Files()
                .Select(f => f.RelativePath.EndsWith("/Guide.md", StringComparison.Ordinal)
                    ? new PackageFile(f.RelativePath, $"# Guide\n\n{ImpostorMarker}.")
                    : f)
                .ToList());
    }

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                // Order is the configured name: the impostor is registered-0 and listed FIRST.
                .AddSingleton<IPackageSource>(_ => impostor)
                .AddSingleton<IPackageSource>(_ => new ReassertFixture.Source(ReassertFixture.Files()))
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false }));

    [Fact(Timeout = 300_000)]
    public async Task ARecordNamingItsSource_IsRepairedFromThatSource_NotFromOneThatAuthorsItsName()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger<ARepairBindsToATrustedSourceTest>();
        await Mesh.ServiceProvider.GetRequiredService<InstanceAutoRegistrationService>().Completed
            .FirstAsync().Timeout(180.Seconds()).Await(ct);

        // Installed from the honest source, the record stamped with its configured name.
        await CatalogLayoutAreas
            .InstallOrUpdate(Mesh, new ReassertFixture.Source(ReassertFixture.Files()), "HEAD",
                ReassertFixture.Candidate() with { Source = HonestSource }, logger)
            .Should().Within(180.Seconds()).Emit("the install must land first", cancellationToken: ct);
        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.GuidePath, present: true)).Should().BeTrue();
        (await ReassertFixture.ReadRecord(Mesh))!.Source.Should().Be(HonestSource,
            "CONTROL — the record names the source the repair must bind to");

        impostor.StartListing();
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>().DeleteNode(ReassertFixture.GuidePath)
            .Should().Within(TestTimeouts.Convergence).Emit("the loss is the precondition", cancellationToken: ct);
        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.GuidePath, present: false)).Should().BeTrue();

        await Mesh.ServiceProvider.GetRequiredService<InstalledPackageRepairService>()
            .RunRepair().Timeout(240.Seconds()).Await(ct);

        (await ReassertFixture.WaitForNode(Mesh, ReassertFixture.GuidePath, present: true)).Should().BeTrue(
            "the honest source still lists the package, so the repair re-asserts it");
        var guide = await ReassertFixture.Read(Mesh, ReassertFixture.GuidePath);
        var body = JsonSerializer.Serialize(guide!.Content, Mesh.JsonSerializerOptions);
        body.Should().NotContain(ImpostorMarker,
            "a repair must never land the files of a repository that merely AUTHORS the recorded source");
        body.Should().Contain("The node the tests lose",
            "the node comes back from the source the record names");
    }
}
