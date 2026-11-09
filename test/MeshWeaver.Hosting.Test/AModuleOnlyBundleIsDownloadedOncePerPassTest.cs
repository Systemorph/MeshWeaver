using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A module-only bundle is downloaded ONCE per default-install pass, not twice.
///
/// <para><b>The measured case</b> (a local Mac instance's Loki, 2026-11-09): the module phase
/// downloaded each package's bundle to land its module, and the node install then asked
/// <see cref="PluginBundleClient.Adopt"/> for the package's prebuilt NodeTypes — which downloaded
/// the same bytes again only to log "declares no NodeTypes — nothing to adopt". AzureFoundry,
/// 22,988,649 bytes: 22.0 s, then 22.4 s; Chat, Observability, Mcp, Notifications, Teams, OpenAI,
/// WebSearch the same.</para>
///
/// <para><b>Pinned:</b> after the module landing has READ the bundle, the NodeType adoption of the
/// same package and version answers "nothing to adopt" without a request; a DIFFERENT client of
/// the same mesh (the node install holds its own) shares the fact; and a bundle that DOES declare
/// something for the NodeType lane (a stated miss) is still downloaded — the negative control.</para>
/// </summary>
public class AModuleOnlyBundleIsDownloadedOncePerPassTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string Package = "BootTestModule";
    private const string Module = "MeshWeaver.BootTestModule";
    private const string Version = "1.2.3";

    private readonly string moduleRoot =
        Path.Combine(Path.GetTempPath(), "mw-once-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly FakeRegistry registry = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                .AddSingleton(_ => new ModuleLandingService(baseDirectory: moduleRoot))
                .AddSingleton<IHttpClientFactory>(new FakeFactory(registry)));

    [Fact(Timeout = 300_000)]
    public async Task TheNodeTypeAdoptionReusesWhatTheModuleLandingRead()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Bundle = ModuleBundle(misses: null);

        // The module phase: its own client, as LandModulesFirst holds one per source.
        await new PluginBundleClient(Mesh, FakeRegistry.Url, "mwi_test")
            .AdoptModuleOutcome(Package, Module, $"{PackageInstaller.InstalledPartition}/{Package}")
            .Should().Within(TestTimeouts.Convergence).Emit("the module adoption answers once", ct);
        registry.Downloads.Should().Be(1, "the module phase downloads the bundle once");

        // The node install: a DIFFERENT client of the same mesh.
        var adopted = await new PluginBundleClient(Mesh, FakeRegistry.Url, "mwi_test").Adopt(Package)
            .Should().Within(TestTimeouts.Convergence).Emit("the NodeType adoption answers once", ct);

        adopted.Should().Be(0, "a module-only bundle has no NodeType to adopt");
        registry.Downloads.Should().Be(1,
            "the bundle was already read by the module landing — the second download only ever learned 'no NodeTypes'");
    }

    [Fact(Timeout = 300_000)]
    public async Task ABundleWithAStatedMissIsStillDownloadedForTheNodeTypeLane()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Bundle = ModuleBundle(misses: ["Type/Missing: no artifact for this lane"]);

        await new PluginBundleClient(Mesh, FakeRegistry.Url, "mwi_test")
            .AdoptModuleOutcome(Package, Module, $"{PackageInstaller.InstalledPartition}/{Package}")
            .Should().Within(TestTimeouts.Convergence).Emit("the module adoption answers once", ct);
        await new PluginBundleClient(Mesh, FakeRegistry.Url, "mwi_test").Adopt(Package)
            .Should().Within(TestTimeouts.Convergence).Emit("the NodeType adoption answers once", ct);

        registry.Downloads.Should().Be(2,
            "a bundle that states something for the NodeType lane is not 'nothing to adopt' — it is asked again");
    }

    /// <summary>A module bundle as the pack lane writes it, carrying a real managed assembly so the
    /// landing's link probe can read it.</summary>
    private static byte[] ModuleBundle(string[]? misses)
    {
        var manifest = new BundleReader.Manifest(
            Plugin: Package, Version: Version, FrameworkMvid: PrebuiltAssemblySeeder.LiveFrameworkMvid, Assemblies: [],
            Module: new BundleReader.ModuleRef(Module, [Module + ".dll"]), Misses: misses);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry(NuGetPackageWriter.ManifestEntry).Open()))
                writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            using var entry = archive.CreateEntry(NuGetPackageWriter.ModuleEntryPathFor(Module + ".dll")).Open();
            var bytes = File.ReadAllBytes(typeof(BundleReader).Assembly.Location);
            entry.Write(bytes, 0, bytes.Length);
        }
        return stream.ToArray();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            if (Directory.Exists(moduleRoot))
                Directory.Delete(moduleRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory no later run reads.
        }
    }

    /// <summary>The bundle routes: an index naming the one module bundle, and its download, counted.</summary>
    private sealed class FakeRegistry : HttpMessageHandler
    {
        public const string Url = "https://registry.test";
        private int downloads;

        public byte[] Bundle { get; set; } = [];

        public int Downloads => Volatile.Read(ref downloads);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/index.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"frameworkMvid\":\"" + PrebuiltAssemblySeeder.LiveFrameworkMvid + "\",\"bundles\":[{\"plugin\":\""
                        + Package + "\",\"version\":\"" + Version + "\",\"url\":\"" + Url + PluginBundleClient.RoutePrefix
                        + "/" + Package + "/" + Version + "\",\"module\":\"" + Module + "\"}]}",
                        Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
                });
            if (path.StartsWith(PluginBundleClient.RoutePrefix + "/" + Package + "/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref downloads);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Bundle),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
