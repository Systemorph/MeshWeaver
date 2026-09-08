#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PackagingManifest = MeshWeaver.Plugin.Packaging.PluginManifest;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// <see cref="PluginBundleClient"/> learning the artifact path
/// (<c>Doc/Architecture/PluginBundlesInTheRegistry</c>): when the bundle index names an
/// <c>artifact</c>, the bytes come from the OCI registry — manifest by digest, layer by digest,
/// both verified — and enter the SAME landing path as before; when it names none, the HTTP bundle
/// route is taken exactly as today; and when the OCI registry serves bytes that do not hash to
/// the sealed digest, NOTHING lands — no file, no sidecar entry, no HTTP fallback — and the ledger
/// says why.
///
/// <para>The witnesses are the two registries' own request logs: which route was asked for is the
/// one thing the consumer cannot fake. The hub, the landing service and the update decision are
/// real; only the network is a fake.</para>
/// </summary>
public class PluginBundleArtifactFetchTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string RegistryHost = "registry.artifact.test";
    private const string RegistryUrl = "https://" + RegistryHost;
    private const string OciHost = "cr.artifact.test";
    private const string Token = "mwi_artifact_consumer";
    private const string Plugin = "ArtifactPkg";
    private const string Module = "MeshWeaver.ArtifactModule";
    private const string Version = "1.2.0";
    private const string ServedFramework = "s1234567890abcdef1234567890abcdef";

    private readonly string landingRoot =
        Path.Combine(Path.GetTempPath(), "mw-artifact-fetch-" + Guid.NewGuid().ToString("N"));

    // One packed bundle, shared by both fakes: the OCI registry serves it as the artifact's layer,
    // the plugin registry serves it on the HTTP route. Immutable bytes, never written after
    // construction — the same shape as a media-type table.
    private static readonly byte[] BundleBytes = ModuleBundle();

    private readonly FakeOciRegistry oci = new(OciHost, Token, $"plugins/Plugins/{Plugin}", BundleBytes);
    private readonly FakePluginRegistry registry = new(BundleBytes);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        Directory.CreateDirectory(landingRoot);
        return base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton<IHttpClientFactory>(new HostRoutingClientFactory(oci, registry))
                // Land into a per-test temp tree, never the test host's own bin folder.
                .AddSingleton(new ModuleLandingService(baseDirectory: landingRoot)));
    }

    /// <inheritdoc />
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
            // Best-effort: a cleanup hiccup must never turn a green test red.
        }
    }

    private PluginBundleClient Client() => new(Mesh, RegistryUrl, Token);

    private BundleAdoptionLedger Ledger => Mesh.ServiceProvider.GetRequiredService<BundleAdoptionLedger>();

    private string LandedDll()
    {
        var list = ModuleActivationSidecar.Read(landingRoot);
        var entry = list.Entries.Single(e => e.Name == Module);
        return Path.Combine(ModuleLandingService.ModuleDirectoryFor(landingRoot, Module, entry), Module + ".dll");
    }

    [Fact(Timeout = 120_000)]
    public async Task AnAdvertisedArtifact_IsFetchedFromTheOciRegistry_NeverTheHttpRoute()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Artifact = oci.Reference;

        var landed = await Client().AdoptModule(Plugin, Module, "Plugins/" + Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        landed.Should().Be(1, "the module file in the artifact's bundle layer lands");
        File.Exists(LandedDll()).Should().BeTrue();
        registry.BundleDownloads.Should().BeEmpty(
            "with an artifact advertised the HTTP bundle route is not asked at all");
        oci.ManifestRequests.Should().Be(1);
        oci.BlobRequests.Should().Be(1);
        oci.PresentedSecret.Should().Be(Token,
            "the credential at the OCI realm is the plugin-registry token the client already holds");
        oci.PresentedUser.Should().Be(OciRegistryClient.DefaultUsername);
        Ledger.Outcomes.Should().NotContain(o => o.PluginId == Plugin && o.IsMiss);
    }

    [Fact(Timeout = 120_000)]
    public async Task ATamperedArtifact_LandsNothing_AndDoesNotFallBackToHttp()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Artifact = oci.Reference;
        oci.TamperBlob = true;

        var landed = await Client().AdoptModule(Plugin, Module, "Plugins/" + Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        landed.Should().Be(0);
        ModuleActivationSidecar.Read(landingRoot).Entries.Should().BeEmpty("nothing landed");
        Directory.EnumerateFiles(landingRoot, "*.dll", SearchOption.AllDirectories).Should().BeEmpty(
            "not one byte of a bundle that failed its digest reaches the module tree");
        registry.BundleDownloads.Should().BeEmpty(
            "a refusal is not a hiccup — the HTTP route is never a fallback for bytes that failed their digest");
        var miss = Ledger.Outcomes.Should().ContainSingle(o => o.PluginId == Plugin).Subject;
        miss.Kind.Should().Be(BundleAdoptionKind.ArtifactRefused);
        miss.Reason.Should().Contain(oci.BundleDigest);
    }

    [Fact(Timeout = 120_000)]
    public async Task WithoutAnArtifact_TheHttpRouteIsTaken_AsToday()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Artifact = null;

        var landed = await Client().AdoptModule(Plugin, Module, "Plugins/" + Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        landed.Should().Be(1);
        File.Exists(LandedDll()).Should().BeTrue();
        registry.BundleDownloads.Should().ContainSingle().Which.Should().Be(Plugin);
        oci.Requests.Should().BeEmpty("an index without an artifact never reaches the OCI registry");
    }

    /// <summary>A packed module bundle carrying REAL assembly bytes — the landing measures them
    /// (#3538), so a byte stand-in would be refused as unreadable and the test would be about that
    /// gate instead of the fetch.</summary>
    private static byte[] ModuleBundle()
    {
        var manifestJson = JsonSerializer.Serialize(new
        {
            plugin = Plugin,
            version = Version,
            frameworkMvid = ServedFramework,
            module = new
            {
                assemblyName = Module,
                assemblies = new[] { Module + ".dll" },
            },
        });

        var buffer = new MemoryStream();
        NuGetPackageWriter.Write(
            buffer,
            new PackagingManifest(Plugin, "MeshWeaver.Plugin." + Plugin, Version, Plugin, null, []),
            "3.0.0",
            [
                new NuGetPackageWriter.Entry(
                    NuGetPackageWriter.ModuleEntryPathFor(Module + ".dll"),
                    () => new MemoryStream(File.ReadAllBytes(typeof(BundleReader).Assembly.Location))),
            ],
            manifestJson);
        return buffer.ToArray();
    }

    /// <summary>
    /// The plugin registry as the bundle client reaches it: the bundle index advertising ONE
    /// module bundle — with or without an <c>artifact</c> — and the HTTP bundle route serving the
    /// same bytes the OCI fake holds, RECORDING every download, which is the witness.
    /// </summary>
    private sealed class FakePluginRegistry(byte[] bundle) : HttpMessageHandler, IHostHandler
    {
        private ImmutableList<string> downloads = ImmutableList<string>.Empty;

        public string? Artifact;

        public ImmutableList<string> BundleDownloads => downloads;

        public bool Serves(string host) => string.Equals(host, RegistryHost, StringComparison.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == $"{PluginBundleClient.RoutePrefix}/index.json")
            {
                var body = JsonSerializer.Serialize(new
                {
                    frameworkMvid = ServedFramework,
                    bundles = new[]
                    {
                        new
                        {
                            plugin = Plugin,
                            version = Version,
                            url = $"{RegistryUrl}{PluginBundleClient.RoutePrefix}/{Plugin}/{Version}",
                            module = Module,
                            frameworkMvid = ServedFramework,
                            artifact = Artifact,
                        },
                    },
                }, PluginRegistryPayloads.Json);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Get && path.StartsWith($"{PluginBundleClient.RoutePrefix}/", StringComparison.Ordinal))
            {
                var package = path[(PluginBundleClient.RoutePrefix.Length + 1)..].Split('/')[0];
                ImmutableInterlocked.Update(ref downloads, d => d.Add(Uri.UnescapeDataString(package)));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bundle),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
