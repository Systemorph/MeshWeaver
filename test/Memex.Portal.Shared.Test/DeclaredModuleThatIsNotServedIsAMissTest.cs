#pragma warning disable CS1591

using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// A package that DECLARES a module the registry cannot serve at the requested version (the package
/// record is ahead of the module's own publish) used to be answered with a manifest that declared
/// nothing at all: no module section (written only when files exist), no miss. Every consumer read
/// it as an empty archive — <c>/health</c> bundle_adoption Degraded, "the bundle carried no
/// assemblies", an Information log and no producer warning — indistinguishable from a producer
/// that shipped an empty archive. The registry must state the decline as a
/// <c>module '&lt;name&gt;': not served — …</c> miss, and the consumer must carry it into its reason.
/// Real authenticated route, real install record, real (empty) module shelf.
/// </summary>
public class DeclaredModuleThatIsNotServedIsAMissTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Plugin = "LaggingModulePkg";
    private const string Source = "Plugins";
    private const string Version = "1.37.5";
    private const string ModuleName = "MeshWeaver.LaggingModule";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddPluginCatalog();

    [Fact(Timeout = 300_000)]
    public async Task ADeclaredModuleTheShelfDoesNotHold_IsStatedAsAMissOnTheWire()
    {
        var ct = TestContext.Current.CancellationToken;
        await InstallRecord(ct);
        var key = await RegisterInstance(ct);
        await using var app = await StartBundleHost(ct);

        var (manifest, assemblies) = BundleReader.Read(await Bundle(app, key, ct));

        Assert.NotNull(manifest);
        Assert.Empty(assemblies);
        var miss = Assert.Single(manifest!.Misses ?? []);
        Assert.True(BundleReader.IsModuleNotServedMiss(miss), miss);
        Assert.Contains($"module '{ModuleName}'", miss);
        Assert.Contains(Version, miss);

        // The consumer's rule is untouched: still NOT "nothing to adopt" — but now it has a reason.
        Assert.Equal(MeshWeaver.PluginCatalog.BundleAdoptionKind.NoAssemblies,
            BundleOffering.ClassifyEmpty(manifest));
    }

    [Fact]
    public void ANotServedModuleMiss_IsNotNodeTypeCoverage_ButADivergenceStillIs()
    {
        Assert.True(BundleReader.IsModuleNotServedMiss(
            $"module 'M': {BundleReader.ModuleNotServedReason}the shelf holds no generation"));
        Assert.False(BundleReader.IsModuleNotServedMiss("module 'M': the assemblies were built against another module"));
        Assert.False(BundleReader.IsModuleNotServedMiss("Pkg/Type: no artifact for this lane"));
        Assert.False(BundleReader.IsModuleNotServedMiss(null));
    }

    private Task<InstallResult> InstallRecord(CancellationToken ct) =>
        PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = Plugin,
                    Name = Plugin,
                    Kind = PackageKind.Content,
                    TargetPartition = Plugin,
                    SourceFolder = Plugin,
                    Version = "content-path-commit",
                    ReleasedVersion = Version,
                    Module = ModuleName,
                    Source = Source,
                },
                [new PackageFile($"{Plugin}/Doc.md", "# Lagging module")],
                "HEAD")
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private Task<string> RegisterInstance(CancellationToken ct) =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:0"] = $"{Source}/*",
                    })
                    .Build())
            .Register("lag-owner", "Lag Owner", "owner@test.com", "lag-consumer", "lag-consumer")
            .Select(result => result.RawKey)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private async Task<WebApplication> StartBundleHost(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(Mesh.ServiceProvider.GetRequiredService<InstanceRegistryAuthenticator>());
        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync(ct);
        return app;
    }

    private static async Task<byte[]> Bundle(WebApplication app, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PluginBundleEndpoints.RoutePrefix}/{Plugin}/{Version}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        using var response = await app.GetTestClient().SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
