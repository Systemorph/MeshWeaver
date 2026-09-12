#pragma warning disable CS1591

using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using Memex.Portal.Shared.Api;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// #3996's registry-serving half: keeping a late older publication behind a newer activation head
/// must not turn that retained generation into dead stock. The real authenticated bundle routes
/// advertise both versions and return the bytes of the generation each URL names.
/// </summary>
public class PublishedModuleVersionShelfTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Plugin = "VersionedShelf";
    private const string Module = "MeshWeaver.VersionedShelf";
    private const string Source = "Plugins";
    private const string Instance = "versioned-shelf-consumer";
    private const string NewerVersion = "1.7.0";
    private const string OlderVersion = "1.6.1";
    private const string MarkerPath = "wwwroot/generation.txt";

    private readonly string landingRoot = Path.Combine(
        Path.GetTempPath(), "mw-versioned-shelf-" + Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(landingRoot);
        return base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new ModuleLandingService(baseDirectory: landingRoot)));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(landingRoot))
                Directory.Delete(landingRoot, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup must never turn a passing distribution assertion red.
        }
    }

    private Task<string> RegisterInstance() =>
        new MeshWeaverInstanceService(
                Mesh.ServiceProvider.GetRequiredService<MeshWeaver.Mesh.Services.IMeshService>(),
                Mesh,
                Mesh.ServiceProvider.GetRequiredService<ILogger<MeshWeaverInstanceService>>(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"{MeshWeaverInstanceService.DefaultGrantsConfigKey}:0"] = $"{Source}/*",
                    })
                    .Build())
            .Register("shelf-owner", "Shelf Owner", "owner@test.com", Instance, Instance)
            .Select(result => result.RawKey)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await();

    private async Task<WebApplication> StartBundleHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(
            Mesh.ServiceProvider.GetRequiredService<InstanceRegistryAuthenticator>());
        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync();
        return app;
    }

    private async Task Shelve(string version, string marker)
    {
        var assembly = File.ReadAllBytes(typeof(BundleReader).Assembly.Location);
        await Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>()
            .ShelveModule(
                Module,
                [(Module + ".dll", assembly)],
                frameworkMvid: "framework-" + version,
                packagePath: $"{Source}/{Plugin}",
                version: version,
                staticAssets: [(MarkerPath, Encoding.UTF8.GetBytes(marker))])
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence);
    }

    private Task<InstallResult> InstallOlderRecord() =>
        PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = Plugin,
                    Name = Plugin,
                    Kind = PackageKind.Content,
                    TargetPartition = Plugin,
                    SourceFolder = Plugin,
                    Version = "older-source-commit",
                    ReleasedVersion = OlderVersion,
                    Source = Source,
                    Module = Module,
                },
                [new PackageFile($"{Plugin}/Doc.md", "# Versioned shelf")],
                "HEAD")
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await();

    private static async Task<HttpResponseMessage> Get(
        WebApplication app, string route, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return await app.GetTestClient().SendAsync(request);
    }

    [Fact(Timeout = 300_000)]
    public async Task HeadAndFallback_AreIndexedNewestFirst_AndEachUrlServesItsGeneration()
    {
        await Shelve(NewerVersion, "newer");
        await Shelve(OlderVersion, "older");
        await InstallOlderRecord();
        var activation = Assert.Single(ModuleActivationSidecar.Read(landingRoot).Entries);
        Assert.Equal(NewerVersion, activation.Version);
        Assert.Equal(OlderVersion, activation.PreviousVersion);

        var key = await RegisterInstance();
        await using var app = await StartBundleHost();

        using var indexResponse = await Get(
            app, PluginBundleEndpoints.RoutePrefix + "/index.json", key);
        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        using var index = JsonDocument.Parse(await indexResponse.Content.ReadAsStringAsync());
        var entries = index.RootElement.GetProperty("bundles").EnumerateArray()
            .Where(bundle => string.Equals(
                bundle.GetProperty("plugin").GetString(), Plugin,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(NewerVersion, entries[0].GetProperty("version").GetString());
        Assert.Equal(OlderVersion, entries[1].GetProperty("version").GetString());
        Assert.All(entries, entry => Assert.Equal(Module,
            entry.GetProperty("module").GetString()));

        Assert.Equal("newer", await ServedMarker(app, NewerVersion, key));
        Assert.Equal("older", await ServedMarker(app, OlderVersion, key));
    }

    private static async Task<string> ServedMarker(
        WebApplication app, string version, string key)
    {
        using var response = await Get(
            app, $"{PluginBundleEndpoints.RoutePrefix}/{Plugin}/{version}", key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var assets = BundleReader.ReadModuleAssets(await response.Content.ReadAsByteArrayAsync());
        var marker = Assert.Single(assets, asset => asset.RelativePath == MarkerPath);
        return Encoding.UTF8.GetString(marker.Bytes);
    }
}
