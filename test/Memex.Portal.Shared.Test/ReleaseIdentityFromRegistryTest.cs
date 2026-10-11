#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Plugin.Packaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A published root no publisher writes to still learns a release's framework identity</b>
/// (policy <c>release-identity-from-registry</c>).
///
/// <para>The estate under test is the registry-fed one: the published root holds the bundles of the
/// identity the running image reports and NO <c>_releases</c> directory, because the publisher
/// writes markers to storage this installation does not mount. Before this, every candidate release
/// answered "identity not resolvable" there and the installation held on the release it booted
/// with. What is pinned: the registry's <c>plugins/releases:&lt;version&gt;</c> record supplies the
/// identity when — and only when — the marker file is absent; the installation's key goes to the
/// artifact host the registry mount declares and to no other; and every way of not knowing leaves
/// the hold in place.</para>
/// </summary>
public class ReleaseIdentityFromRegistryTest(ITestOutputHelper output) : MonolithMeshTestBase(output), IDisposable
{
    private const string RegistryUrl = "https://registry.release-identity.test";
    private const string ArtifactHost = "cr.release-identity.test";
    private const string Key = "mwi_release-identity-key";
    private const string Release = "3.1.10517";
    private const string Identity = "c003e001";
    private const string Package = "ReleaseIdentityPackage";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private readonly FakeBundleIndex index = new(new Uri(RegistryUrl).Host) { ArtifactRegistry = ArtifactHost };
    private readonly FakeReleaseRecords records = new(ArtifactHost, Key);
    private readonly string root = Path.Combine(Path.GetTempPath(), "rifr-" + Guid.NewGuid().ToString("N"));

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(s => s
                .AddSingleton<IHttpClientFactory>(new HostRoutingClientFactory(index, records))
                .AddSingleton(new PluginCatalogOptions
                {
                    Registries = [new PluginRegistryReference { Name = "fleet", Url = RegistryUrl, Token = Key }],
                }));

    void IDisposable.Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* a temp directory the OS will reap; nothing under test depends on it */ }
    }

    private RegistryReleaseIdentities Identities => new(Mesh);

    private Task<RegistryReleaseIdentities.ReleaseRecord?> Resolve(string version) =>
        Identities.Resolve(version).FirstAsync().Timeout(Budget).Await(TestContext.Current.CancellationToken);

    // ── the record ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARecordIsParsed_OnlyWhenItNamesTheVersionAsked_WithAnIdentityThatIsOnePathSegment()
    {
        static RegistryReleaseIdentities.ReleaseRecord? Parse(string json, string version = Release) =>
            RegistryReleaseIdentities.ReleaseRecord.Parse(Encoding.UTF8.GetBytes(json), version);

        var plain = Parse($$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");
        Assert.Equal(Identity, plain!.Identity);
        Assert.Null(plain.ExpectedDbVersion);

        Assert.Equal(61, Parse($$"""{"identity":"{{Identity}}","version":"{{Release}}","expectedDbVersion":61}""")!.ExpectedDbVersion);

        // Another release's record under this tag is not this release's identity.
        Assert.Null(Parse($$"""{"identity":"{{Identity}}","version":"3.1.1"}"""));
        // The identity becomes a directory name under the published root.
        Assert.Null(Parse($$"""{"identity":"../etc","version":"{{Release}}"}"""));
        Assert.Null(Parse($$"""{"identity":"a/b","version":"{{Release}}"}"""));
        Assert.Null(Parse($$"""{"identity":"..","version":"{{Release}}"}"""));
        Assert.Null(Parse($$"""{"identity":"_releases","version":"{{Release}}"}"""));
        Assert.Null(Parse($$"""{"identity":"","version":"{{Release}}"}"""));
        Assert.Null(Parse($$"""{"version":"{{Release}}"}"""));
        Assert.Null(Parse("""["not","a","record"]"""));
        Assert.Null(Parse("not json"));
    }

    // ── the registry read ─────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 120_000)]
    public async Task TheIdentityIsReadFromTheHostTheRegistryMountDeclares_WithThisInstallationsKey()
    {
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}","expectedDbVersion":61}""");

        var record = await Resolve(Release);

        Assert.NotNull(record);
        Assert.Equal(Identity, record.Identity);
        Assert.Equal(61, record.ExpectedDbVersion);
        Assert.Equal(Key, records.PresentedSecret);
        Assert.Contains(records.Requests, r => r.EndsWith($"/v2/plugins/releases/manifests/{Release}", StringComparison.Ordinal));
    }

    [Fact(Timeout = 120_000)]
    public async Task ARegistryThatDeclaresNoArtifactHost_IsNotGuessedAt()
    {
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");
        index.ArtifactRegistry = null;

        Assert.Null(await Resolve(Release));
        // The key was never presented anywhere but the mount it belongs to.
        Assert.Empty(records.Requests);
    }

    [Fact(Timeout = 120_000)]
    public async Task EveryWayOfNotKnowing_AnswersNothing_AndNeverFaults()
    {
        // No record for the release at all — published before the record existed.
        Assert.Null(await Resolve(Release));

        // A record that names another release.
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"3.1.1"}""");
        Assert.Null(await Resolve(Release));

        // Something else under the tag — not a release record.
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""", mediaType: OciRegistryClient.PublicationConfigMediaType);
        Assert.Null(await Resolve(Release));

        // Bytes that do not hash to the digest the manifest names.
        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");
        records.TamperConfig = true;
        Assert.Null(await Resolve(Release));
        records.TamperConfig = false;

        // The registry mount is down.
        index.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Null(await Resolve(Release));
        index.Status = null;

        // A version that is not a tag is never sent anywhere.
        var before = records.Requests.Count;
        Assert.Null(await Resolve("../3.1.10517"));
        Assert.Null(await Resolve("3.1.10517+abc"));
        Assert.Equal(before, records.Requests.Count);

        // And with the record intact again, the same reader answers.
        Assert.Equal(Identity, (await Resolve(Release))!.Identity);
    }

    // ── the catalogue ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheRegistryIdentityStandsInForAnAbsentMarker_AndNeverOverridesOneThatExists()
    {
        Seal(root, Identity, "plugins", [Package]);

        // No marker, nothing from the registry: the answer every candidate got on this estate.
        var unknown = PublishedBundleCatalogue.Read(root, Release);
        Assert.Null(unknown.Target.FrameworkIdentity);
        Assert.False(PublishedBundleCatalogue.HasReleaseMarker(root, Release));

        // No marker, the registry names the identity: the bundles sealed under it are observed.
        var resolved = PublishedBundleCatalogue.ReadWithRegistryIdentity(root, Release, Identity);
        Assert.Equal(Identity, resolved.Target.FrameworkIdentity);
        Assert.Contains(Package, resolved.Artifacts.SealedBundles);
        // Nothing was written — the root may be read-only, and the file would outlive the answer.
        Assert.False(Directory.Exists(Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName)));

        // An identity nothing is published under clears nothing.
        var elsewhere = PublishedBundleCatalogue.ReadWithRegistryIdentity(root, Release, "d00dfeed");
        Assert.Equal("d00dfeed", elsewhere.Target.FrameworkIdentity);
        Assert.DoesNotContain(Package, elsewhere.Artifacts.SealedBundles);

        // The publisher's marker wins over whatever the registry says.
        Seal(root, "published", "plugins", [Package]);
        var markers = Path.Combine(root, SealedPublicationIndex.ReleaseMarkerDirectoryName);
        Directory.CreateDirectory(markers);
        File.WriteAllText(Path.Combine(markers, Release), "published\n");
        Assert.True(PublishedBundleCatalogue.HasReleaseMarker(root, Release));
        Assert.Equal("published",
            PublishedBundleCatalogue.ReadWithRegistryIdentity(root, Release, Identity).Target.FrameworkIdentity);
    }

    // ── the gate ──────────────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 240_000)]
    public async Task TheGateHoldsWhileNothingNamesTheIdentity_AndClearsOnceTheRegistryDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        await PackageInstaller.Install(
                Mesh,
                new PackageManifest
                {
                    Id = Package, Name = Package, Kind = PackageKind.Content,
                    TargetPartition = Package, SourceFolder = Package,
                    Version = "1.0.0", ReleasedVersion = "1.0.0", Source = "plugins",
                },
                [new PackageFile($"{Package}/Doc.md", $"# {Package}")],
                "HEAD")
            .FirstAsync().Timeout(TimeSpan.FromSeconds(120)).Await(ct);

        // The registry-fed root: this image's identity, sealed, and no _releases directory.
        Seal(root, Identity, "plugins", [Package]);
        var gate = new ReleaseAvailabilityService(
            Mesh,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ShippedPrebuiltBundles.PublishedRootConfigKey] = root,
                })
                .Build());

        var held = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.False(held.IsUpdatable);
        Output.WriteLine($"held: {held.HoldReason}");

        records.Publish(Release, $$"""{"identity":"{{Identity}}","version":"{{Release}}"}""");

        var cleared = await gate.IsUpdatable(Release).FirstAsync().Timeout(Budget).Await(ct);
        Assert.True(cleared.IsUpdatable, cleared.HoldReason);
    }

    // ── staging ───────────────────────────────────────────────────────────────────────────────

    private static void Seal(string root, string identity, string source, IEnumerable<string> bundles)
    {
        var directory = Path.Combine(root, identity, source);
        Directory.CreateDirectory(directory);
        var names = new List<string>();
        foreach (var bundle in bundles)
        {
            var manifest = new BundleReader.Manifest(
                bundle, "1.0", identity,
                [
                    new BundleReader.AssemblyRef(
                        $"{bundle}/Type", $"{bundle}_Type.dll",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["MeshWeaver.Layout"] = MeshWeaver.Compiler.CompiledDependencies.RefAsmScheme + "abc",
                        }),
                ]);
            using (var file = File.Create(Path.Combine(directory, bundle + ".zip")))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            using (var stream = zip.CreateEntry(NuGetPackageWriter.ManifestEntry).Open())
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(
                    manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            names.Add(bundle + ".zip");
        }
        File.WriteAllText(
            Path.Combine(directory, ShippedPrebuiltBundles.CompletionSentinelFileName),
            string.Join('\n', names.Order(StringComparer.Ordinal)) + "\n");
    }

    /// <summary>The plugin registry's bundle index, as far as this reader uses it: the artifact
    /// host it declares, behind the instance key.</summary>
    private sealed class FakeBundleIndex(string host) : HttpMessageHandler, IHostHandler
    {
        public string? ArtifactRegistry { get; set; }
        public HttpStatusCode? Status { get; set; }

        public bool Serves(string candidate) => string.Equals(candidate, host, StringComparison.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Status is { } status)
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("down") });
            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: Key })
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (!request.RequestUri!.AbsolutePath.EndsWith("/index.json", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var body = JsonSerializer.Serialize(
                new { frameworkMvid = Identity, bundles = Array.Empty<object>(), artifactRegistry = ArtifactRegistry },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>An OCI registry holding <c>plugins/releases</c>: one layer-less manifest per tag
    /// whose config blob is the release record, behind the bearer challenge the fleet's registry
    /// speaks. It holds the protocol's invariants — a 401 without a bearer, a 401 on a wrong key,
    /// content-addressed blobs — rather than replaying answers.</summary>
    private sealed class FakeReleaseRecords(string host, string key) : HttpMessageHandler, IHostHandler
    {
        private const string Repository = RegistryReleaseIdentities.ReleaseRepository;
        private ImmutableDictionary<string, (byte[] Manifest, byte[] Config)> tags =
            ImmutableDictionary<string, (byte[], byte[])>.Empty;
        private ImmutableList<string> requests = ImmutableList<string>.Empty;

        public bool TamperConfig { get; set; }
        public string? PresentedSecret { get; private set; }
        public ImmutableList<string> Requests => requests;

        public bool Serves(string candidate) => string.Equals(candidate, host, StringComparison.OrdinalIgnoreCase);

        public void Publish(string tag, string record, string mediaType = RegistryReleaseIdentities.ReleaseConfigMediaType)
        {
            var config = Encoding.UTF8.GetBytes(record);
            var manifest = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                mediaType = OciRegistryClient.ImageManifestMediaType,
                artifactType = mediaType,
                config = new { mediaType, digest = OciDigest.Of(config), size = config.Length },
                layers = Array.Empty<object>(),
            }));
            ImmutableInterlocked.Update(ref tags, t => t.SetItem(tag, (manifest, config)));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            ImmutableInterlocked.Update(ref requests, r => r.Add($"{request.Method} {uri.AbsolutePath}"));

            if (uri.AbsolutePath == "/v2/token")
            {
                string? secret = null;
                if (request.Headers.Authorization is { Scheme: "Basic", Parameter: { } basic })
                {
                    var pair = Encoding.UTF8.GetString(Convert.FromBase64String(basic));
                    secret = pair[(pair.IndexOf(':') + 1)..];
                }
                PresentedSecret = secret;
                return Task.FromResult(secret == key
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""{"token":"bearer-for-{{key}}"}""", Encoding.UTF8, "application/json"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (request.Headers.Authorization is not { Scheme: "Bearer" } auth || auth.Parameter != $"bearer-for-{key}")
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Bearer", $"realm=\"https://{host}/v2/token\",service=\"{host}\""));
                return Task.FromResult(challenge);
            }

            var manifests = $"/v2/{Repository}/manifests/";
            if (uri.AbsolutePath.StartsWith(manifests, StringComparison.Ordinal))
            {
                if (!tags.TryGetValue(uri.AbsolutePath[manifests.Length..], out var entry))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(entry.Manifest) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(OciRegistryClient.ImageManifestMediaType);
                response.Headers.TryAddWithoutValidation("Docker-Content-Digest", OciDigest.Of(entry.Manifest));
                return Task.FromResult(response);
            }

            var blobs = $"/v2/{Repository}/blobs/";
            if (uri.AbsolutePath.StartsWith(blobs, StringComparison.Ordinal))
            {
                var digest = uri.AbsolutePath[blobs.Length..];
                var config = tags.Values.Select(t => t.Config).FirstOrDefault(c => OciDigest.Of(c) == digest);
                if (config is null)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var bytes = config;
                if (TamperConfig)
                {
                    bytes = (byte[])config.Clone();
                    bytes[bytes.Length / 2] ^= 0x01;
                }
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
