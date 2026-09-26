#pragma warning disable CS1591

using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
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
/// 🚨 MeshWeaver#3876 on the WIRE — the real authenticated per-package bundle route answers a file
/// it resolved and then could not open with <b>503 + <c>Retry-After: 30</c></b>, never a 500.
///
/// <para>The route lists a module's files when it resolves the bundle and opens them only when the
/// archive is written. "Listed, then gone at the open" is produced here without any seam or timing:
/// a DANGLING symbolic link in the landed generation's <c>wwwroot</c>. The directory walk lists it
/// (it is an entry of the directory) and the open fails with <c>FileNotFoundException</c> — the
/// same exception, from the same frame (<c>NuGetPackageWriter.Write</c> → the entry's open), as the
/// assembly the store evicted in production (<c>Collaboration_Review/v1172-….dll</c>).</para>
/// </summary>
public class BundleServeRaceOnTheRouteTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Plugin = "ServeRace";
    private const string Module = "MeshWeaver.ServeRace";
    private const string Source = "Plugins";
    private const string Instance = "serve-race-consumer";
    private const string Version = "1.0.0";

    private readonly string landingRoot = Path.Combine(
        Path.GetTempPath(), "mw-serve-race-" + Guid.NewGuid().ToString("N"));

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
            // Best-effort temp cleanup must never turn a passing assertion red.
        }
    }

    private Task<string> RegisterInstance(CancellationToken cancellationToken) =>
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
            .Register("race-owner", "Race Owner", "owner@test.com", Instance, Instance)
            .Select(result => result.RawKey)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(cancellationToken);

    private async Task<WebApplication> StartBundleHost(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IMessageHub>(Mesh);
        builder.Services.AddSingleton(
            Mesh.ServiceProvider.GetRequiredService<InstanceRegistryAuthenticator>());
        var app = builder.Build();
        app.MapPluginBundles();
        await app.StartAsync(cancellationToken);
        return app;
    }

    private static async Task<HttpResponseMessage> Get(WebApplication app, string route, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return await app.GetTestClient().SendAsync(request);
    }

    [Fact(Timeout = 300_000)]
    public async Task AFileListedThenGoneAtTheOpen_Is503WithRetryAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        await Mesh.ServiceProvider.GetRequiredService<ModuleLandingService>()
            .ShelveModule(
                Module,
                [(Module + ".dll", File.ReadAllBytes(typeof(BundleReader).Assembly.Location))],
                frameworkMvid: "framework-" + Version,
                packagePath: $"{Source}/{Plugin}",
                version: Version,
                staticAssets: [("wwwroot/app.css", Encoding.UTF8.GetBytes("body{}"))])
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

        var key = await RegisterInstance(ct);
        await using var app = await StartBundleHost(ct);
        var route = $"{PluginBundleEndpoints.RoutePrefix}/{Plugin}/{Version}";

        // Positive control: the healthy bundle is served — the route, the grant and the module
        // resolution all work, so the 503 below can only come from the removed file.
        using (var healthy = await Get(app, route, key))
            Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);

        // A file the walk will LIST and the archive cannot OPEN.
        var generation = Assert.Single(ModuleActivationSidecar.Read(landingRoot).Entries).Directory;
        Assert.False(string.IsNullOrWhiteSpace(generation));
        File.CreateSymbolicLink(
            Path.Combine(landingRoot, "modules", generation!, "wwwroot", "evicted.css"),
            Path.Combine(landingRoot, "no-such-target-" + Guid.NewGuid().ToString("N")));

        using var response = await Get(app, route, key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Delta);
    }
}
