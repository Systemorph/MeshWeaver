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

    /// <summary>
    /// 🚨 MeshWeaver#4123 — the instance key goes to an artifact's OCI host ONLY when the registry it
    /// belongs to declares that host (index-level <c>artifactRegistry</c>) or it is the registry's own
    /// host. A registry that declares none, or declares a DIFFERENT host, never sees the OCI host
    /// asked for anything: the bytes come over the registry's own HTTP route, so the install still
    /// adopts. The declared case is <see cref="AnAdvertisedArtifact_IsFetchedFromTheOciRegistry_NeverTheHttpRoute"/>
    /// — the positive control for both arms below.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(null)]
    [InlineData("cr.someone-else.test")]
    [InlineData("cr.artifact.test.evil")]
    public async Task AnUndeclaredArtifactHost_NeverReceivesTheKey_AndTheHttpRouteServes(string? declared)
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Artifact = oci.Reference;
        registry.ArtifactRegistry = declared;

        var landed = await Client().AdoptModule(Plugin, Module, "Plugins/" + Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        landed.Should().Be(1, "the bundle still lands — over the registry's own HTTP route");
        File.Exists(LandedDll()).Should().BeTrue();
        oci.Requests.Should().BeEmpty(
            $"a registry declaring '{declared ?? "(nothing)"}' never sends this instance's key to {OciHost}");
        oci.PresentedSecret.Should().BeNull("the key was never presented at the undeclared host");
        registry.BundleDownloads.Should().ContainSingle().Which.Should().Be(Plugin);
    }

    /// <summary>The rule itself, pure (MeshWeaver#4123): own host or the index's declared artifact
    /// registry; whole-host and case-insensitive; a suffix-extended host or another port is not it.</summary>
    [Fact]
    public void TheArtifactKeyTarget_IsTheRegistrysOwnHost_OrItsDeclaredArtifactRegistry()
    {
        var artifact = "cr.meshweaver.cloud/plugins/Plugins/X@sha256:" + new string('a', 64);
        PluginBundleClient.ArtifactKeyTarget("https://memex.meshweaver.cloud", "cr.meshweaver.cloud", artifact)
            .Should().BeNull("the registry declares that host");
        PluginBundleClient.ArtifactKeyTarget("https://memex.meshweaver.cloud", "CR.MeshWeaver.Cloud", artifact)
            .Should().BeNull("hosts compare case-insensitively");
        PluginBundleClient.ArtifactKeyTarget("https://memex.meshweaver.cloud", "https://cr.meshweaver.cloud/", artifact)
            .Should().BeNull("a declaration written as a URL names the same host");
        PluginBundleClient.ArtifactKeyTarget("https://cr.meshweaver.cloud", null, artifact)
            .Should().BeNull("the registry's OWN host needs no declaration");

        PluginBundleClient.ArtifactKeyTarget("https://memex.meshweaver.cloud", null, artifact)
            .Should().Contain("declares no artifact registry");
        PluginBundleClient.ArtifactKeyTarget("https://memex.meshweaver.cloud", "cr.other.example", artifact)
            .Should().Contain("declares 'cr.other.example'");
        PluginBundleClient.ArtifactKeyTarget("https://memex.meshweaver.cloud", "meshweaver.cloud", artifact)
            .Should().NotBeNull("a parent domain is a different host");
        PluginBundleClient.ArtifactKeyTarget("https://memex.meshweaver.cloud", "cr.meshweaver.cloud",
                "cr.meshweaver.cloud:5000/plugins/Plugins/X@sha256:" + new string('a', 64))
            .Should().NotBeNull("another port is another endpoint");
    }

    /// <summary>
    /// 🚨 MeshWeaver#6172 — what the adopt OUTCOME says about a failed artifact fetch decides whether a
    /// module reload is <c>Faulted</c> (retried) or <c>Failed</c> (final). A registry that is briefly
    /// down or rate-limiting answers something that clears on its own; a refusal or a tampered
    /// layer is an answer the registry will give again.
    /// </summary>
    [Theory(Timeout = 120_000)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task AFailedArtifactFetch_IsTransient_ExactlyWhenItsStatusMayClear(HttpStatusCode status, bool transient)
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Artifact = oci.Reference;
        oci.BlobStatus = status;

        var outcome = await Client().AdoptModuleOutcome(Plugin, Module, "Plugins/" + Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        outcome.Failure.Should().NotBeNull($"the premise: a blob answering {(int)status} lands nothing");
        outcome.Transient.Should().Be(transient,
            $"{(int)status} {(transient ? "may clear on its own — retried" : "is a decided answer — final")}: {outcome.Failure}");
    }

    /// <summary>A layer that fails its digest is a REFUSAL — the registry served bytes that are not the
    /// artifact, and it will serve the same bytes next time: final, never retried.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ATamperedArtifact_IsADecidedFailure_NotTransient()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.Artifact = oci.Reference;
        oci.TamperBlob = true;

        var outcome = await Client().AdoptModuleOutcome(Plugin, Module, "Plugins/" + Plugin)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

        outcome.Failure.Should().Contain("ArtifactRefused");
        outcome.Transient.Should().BeFalse("a hash mismatch is the registry's decided answer");
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

        /// <summary>The index-level <c>artifactRegistry</c> this registry declares (MeshWeaver#4123).
        /// Defaults to the OCI fake's host, the fleet's shape; a test clears or changes it.</summary>
        public string? ArtifactRegistry = OciHost;

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
                    artifactRegistry = ArtifactRegistry,
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
