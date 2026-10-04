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
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#6067 — THE ORIGIN of the mislabel, pinned on the real authenticated routes. A
/// registry's install record advanced to 1.20.1 through the node-content path while its shelf
/// still held the module bytes published at 1.19.4. The index advertised the package at 1.20.1
/// WITH a module section, and the 1.20.1 download served the 1.19.4 bytes — because a record
/// entry carries no shelf version and the null-version lookup followed the head. Consumers landed
/// those bytes stamped 1.20.1.
///
/// <para>The rule now: an entry's module section is served only from the generation shelved AT
/// that entry's version; the bytes stay advertised under the version they were shelved at, and
/// the served archive states that version for the consumer to compare. The consumer half — the
/// landing refuses a bundle whose declared version disagrees with the advertised one — is pinned
/// through the real client at the end.</para>
/// </summary>
public class RegistryNeverMislabelsModuleBytesTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Plugin = "LabelledAi";
    private const string Module = "MeshWeaver.LabelledAi";
    private const string Source = "Plugins";
    private const string Instance = "label-truth-consumer";
    private const string ShelvedVersion = "1.19.4";
    private const string RecordVersion = "1.20.1";
    private const string MarkerPath = "wwwroot/generation.txt";

    private readonly string landingRoot = Path.Combine(
        Path.GetTempPath(), "mw-label-truth-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// The registry state measured on 2026-10-04: the shelf holds the module at 1.19.4, the
    /// install record says 1.20.1. The index must not offer a module section at 1.20.1, the 1.20.1
    /// download must carry no module bytes, and the 1.19.4 bytes stay servable under 1.19.4 with
    /// the archive stating that version.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARecordAheadOfTheShelf_IsNeverServedTheShelfsBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        await Shelve(ShelvedVersion, "bytes-of-" + ShelvedVersion);
        await InstallRecord(RecordVersion, ct);
        Assert.Equal(ShelvedVersion, Assert.Single(ModuleActivationSidecar.Read(landingRoot).Entries).Version);

        var key = await RegisterInstance(ct);
        await using var app = await StartBundleHost(ct);

        using var indexResponse = await Get(app, PluginBundleEndpoints.RoutePrefix + "/index.json", key);
        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        using var index = JsonDocument.Parse(await indexResponse.Content.ReadAsStringAsync(ct));
        var entries = index.RootElement.GetProperty("bundles").EnumerateArray()
            .Where(b => string.Equals(b.GetProperty("plugin").GetString(), Plugin, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(b => b.GetProperty("version").GetString()!, b => b.Clone());

        // The record's version is still advertised (its NodeType content is real) — but with NO
        // module section: the shelf holds no module bytes at that version.
        Assert.True(entries.ContainsKey(RecordVersion));
        Assert.True(
            !entries[RecordVersion].TryGetProperty("module", out var recordModule)
            || recordModule.ValueKind == JsonValueKind.Null,
            $"the index offers module bytes at {RecordVersion}, but the shelf only holds them at {ShelvedVersion}");
        // The shelved bytes stay advertised under the version they were shelved at.
        Assert.Equal(Module, entries[ShelvedVersion].GetProperty("module").GetString());

        // The download at the record's version carries no module bytes at all.
        var (_, recordFiles) = BundleReader.ReadModule(await Bundle(app, RecordVersion, key, ct));
        Assert.Empty(recordFiles);

        // The download at the shelved version carries those bytes AND says which version they are.
        var shelvedBundle = await Bundle(app, ShelvedVersion, key, ct);
        var (shelvedManifest, shelvedFiles) = BundleReader.ReadModule(shelvedBundle);
        Assert.NotEmpty(shelvedFiles);
        Assert.Equal(ShelvedVersion, shelvedManifest!.Module!.Version);
        var marker = Assert.Single(BundleReader.ReadModuleAssets(shelvedBundle), a => a.RelativePath == MarkerPath);
        Assert.Equal("bytes-of-" + ShelvedVersion, Encoding.UTF8.GetString(marker.Bytes));
    }

    /// <summary>The control: when the shelf DOES hold the record's version, the record's entry
    /// serves exactly those bytes, stated at that version — the healthy case is unchanged.</summary>
    [Fact(Timeout = 300_000)]
    public async Task ARecordMatchingTheShelf_IsServedItsOwnGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        await Shelve(RecordVersion, "bytes-of-" + RecordVersion);
        await InstallRecord(RecordVersion, ct);

        var key = await RegisterInstance(ct);
        await using var app = await StartBundleHost(ct);

        var bundle = await Bundle(app, RecordVersion, key, ct);
        var (manifest, files) = BundleReader.ReadModule(bundle);
        Assert.NotEmpty(files);
        Assert.Equal(RecordVersion, manifest!.Module!.Version);
        var marker = Assert.Single(BundleReader.ReadModuleAssets(bundle), a => a.RelativePath == MarkerPath);
        Assert.Equal("bytes-of-" + RecordVersion, Encoding.UTF8.GetString(marker.Bytes));
    }

    /// <summary>
    /// The consumer half through the REAL client: an archive advertised at 1.20.1 whose module
    /// section states 1.19.4 (what a registry that predates the origin fix could still hand out,
    /// with this platform's manifest) lands NOTHING; the same archive advertised at its own version
    /// lands, stamped with the version its bytes declare.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TheLanding_RefusesBytesAdvertisedUnderAnotherVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new PluginBundleClient(Mesh, "http://registry.invalid");
        var bytes = ServedShapeBundle(topLevelVersion: RecordVersion, moduleVersion: ShelvedVersion);

        var refused = await client
            .LandFromBundle(Plugin, Module, $"{Source}/{Plugin}", RecordVersion, bytes, "s-advertised")
            .FirstAsync().Await(ct);
        Assert.Equal(0, refused);
        Assert.Empty(ModuleActivationSidecar.Read(landingRoot).Entries);

        var landed = await client
            .LandFromBundle(Plugin, Module, $"{Source}/{Plugin}", ShelvedVersion,
                ServedShapeBundle(topLevelVersion: ShelvedVersion, moduleVersion: ShelvedVersion), "s-advertised")
            .FirstAsync().Await(ct);
        Assert.Equal(1, landed);
        Assert.Equal(ShelvedVersion, Assert.Single(ModuleActivationSidecar.Read(landingRoot).Entries).Version);
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
            .Timeout(TestTimeouts.Convergence).Await();
    }

    private Task<InstallResult> InstallRecord(string releasedVersion, CancellationToken ct) =>
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
                    ReleasedVersion = releasedVersion,
                    Source = Source,
                    Module = Module,
                },
                [new PackageFile($"{Plugin}/Doc.md", "# Label truth")],
                "HEAD")
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private Task<string> RegisterInstance(CancellationToken ct) =>
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
            .Register("label-owner", "Label Owner", "owner@test.com", Instance, Instance)
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

    private static async Task<HttpResponseMessage> Get(WebApplication app, string route, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return await app.GetTestClient().SendAsync(request);
    }

    private static async Task<byte[]> Bundle(WebApplication app, string version, string key, CancellationToken ct)
    {
        using var response = await Get(app, $"{PluginBundleEndpoints.RoutePrefix}/{Plugin}/{version}", key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>The archive shape a registry serves: the URL's version at the top level, the
    /// shelf generation's version on the module section, REAL assembly bytes (#3538).</summary>
    private static byte[] ServedShapeBundle(string topLevelVersion, string moduleVersion)
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = Plugin,
            version = topLevelVersion,
            frameworkMvid = "s-requested-lane",
            module = new
            {
                assemblyName = Module,
                version = moduleVersion,
                assemblies = new[] { Module + ".dll" },
            },
        });
        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(
            buffer,
            new PackagingManifest(Plugin, "MeshWeaver.Plugin." + Plugin, topLevelVersion, Plugin, null, []),
            "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.ModuleEntryPathFor(Module + ".dll"),
                    () => new MemoryStream(File.ReadAllBytes(typeof(BundleReader).Assembly.Location))),
            ],
            manifestJson);
        return buffer.ToArray();
    }
}
