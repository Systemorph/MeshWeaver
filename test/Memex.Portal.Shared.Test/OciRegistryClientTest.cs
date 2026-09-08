#pragma warning disable CS1591

using System;
using System.IO;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The fleet's one OCI client (<c>Doc/Architecture/PluginBundlesInTheRegistry</c>): the bearer
/// handshake with the instance credential as <c>Basic instance:key</c>, ONE exchange serving a
/// manifest and its blob, a manifest fetched by digest verified against that digest, a blob
/// verified against the digest the manifest names — and a registry that serves OTHER bytes under
/// a sealed digest being REFUSED, for the manifest and for the blob alike.
///
/// <para>The fake substitutes the NETWORK, not a MeshWeaver interface; the hub and its I/O pools
/// are real.</para>
/// </summary>
public class OciRegistryClientTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Host = "cr.example.test";
    private const string Key = "mwi_the-consumers-instance-key";
    private const string Repository = "plugins/Plugins/SocialMedia";

    private static TimeSpan Budget => TestTimeouts.Convergence;

    private static readonly byte[] BundleBytes = System.Text.Encoding.UTF8.GetBytes(
        "not a real zip — the client verifies bytes, it does not open them: " + new string('x', 200_000));

    private readonly FakeOciRegistry registry = new(Host, Key, Repository, BundleBytes);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(new HostRoutingClientFactory(registry)));

    private OciRegistryClient Client(string key = Key) => new(
        Mesh, Host, Observable.Return(key),
        logger: Mesh.ServiceProvider.GetService<ILogger<OciRegistryClient>>());

    [Fact(Timeout = 120_000)]
    public async Task AManifestAndItsBlob_ArriveVerified_ThroughOneExchange()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client();

        var manifest = await client.GetManifest(Repository, registry.ManifestDigest)
            .FirstAsync().Timeout(Budget).Await(ct);

        manifest.Digest.Should().Be(registry.ManifestDigest, "a manifest fetched by digest IS that digest");
        manifest.MediaType.Should().Be(OciRegistryClient.ImageManifestMediaType);
        var layer = manifest.Layers.Should().ContainSingle().Subject;
        layer.MediaType.Should().Be(OciRegistryClient.BundleLayerMediaType);
        layer.Digest.Should().Be(registry.BundleDigest);
        manifest.Config!.MediaType.Should().Be(OciRegistryClient.PublicationConfigMediaType);

        var buffer = new MemoryStream();
        var receipt = await client.GetBlob(Repository, layer.Digest, () => buffer)
            .FirstAsync().Timeout(Budget).Await(ct);

        receipt.Digest.Should().Be(registry.BundleDigest);
        receipt.Size.Should().Be(BundleBytes.Length);
        buffer.ToArray().Should().Equal(BundleBytes, "the destination holds exactly the layer's bytes");

        registry.PresentedUser.Should().Be(OciRegistryClient.DefaultUsername,
            "the registry edge ignores the user name, and every instance presents the same one");
        registry.PresentedSecret.Should().Be(Key,
            "the credential is the plugin-registry token this installation already holds, as the Basic password");
        registry.TokenRequests.Should().Be(1,
            "the bearer from the manifest's challenge is reused for the blob — one exchange per client, per scope");
    }

    [Fact(Timeout = 120_000)]
    public async Task AManifestByTag_IsVerifiedAgainstTheDigestTheRegistryStates()
    {
        var ct = TestContext.Current.CancellationToken;

        var manifest = await Client().GetManifest(Repository, registry.Tag)
            .FirstAsync().Timeout(Budget).Await(ct);

        manifest.Digest.Should().Be(registry.ManifestDigest);
    }

    [Fact(Timeout = 120_000)]
    public async Task ATamperedBlob_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.TamperBlob = true;
        var buffer = new MemoryStream();

        var fault = await Assert.ThrowsAsync<OciDigestMismatchException>(() =>
            Client().GetBlob(Repository, registry.BundleDigest, () => buffer)
                .FirstAsync().Timeout(Budget).Await(ct));

        fault.Expected.Should().Be(registry.BundleDigest);
        fault.Actual.Should().NotBe(registry.BundleDigest);
        fault.Message.Should().Contain("REFUSED");
        registry.BlobRequests.Should().Be(1, "a digest mismatch is never retried — the bytes are wrong, not late");
    }

    [Fact(Timeout = 120_000)]
    public async Task ATamperedManifest_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.TamperManifest = true;

        var fault = await Assert.ThrowsAsync<OciDigestMismatchException>(() =>
            Client().GetManifest(Repository, registry.ManifestDigest)
                .FirstAsync().Timeout(Budget).Await(ct));

        fault.Expected.Should().Be(registry.ManifestDigest);
    }

    [Fact(Timeout = 120_000)]
    public async Task ARefusedCredential_Faults_NamingTheHost()
    {
        var ct = TestContext.Current.CancellationToken;

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client("mwi_not-this-registrys-key").GetManifest(Repository, registry.ManifestDigest)
                .FirstAsync().Timeout(Budget).Await(ct));

        fault.Message.Should().Contain("refused").And.Contain(Host);
        registry.ManifestRequests.Should().Be(0, "nothing is served before the realm accepts the key");
    }

    [Fact(Timeout = 120_000)]
    public async Task EveryTagPage_IsFollowed()
    {
        var ct = TestContext.Current.CancellationToken;

        var tags = await Client().ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct);

        tags.Should().Equal(registry.Tags);
        registry.TokenRequests.Should().Be(1, "one exchange serves every page of one listing");
    }

    [Theory]
    [InlineData("cr.meshweaver.cloud/plugins/Plugins/SocialMedia@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "cr.meshweaver.cloud", "plugins/Plugins/SocialMedia", null,
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("localhost:5000/plugins/Plugins:1.2.3-s1", "localhost:5000", "plugins/Plugins", "1.2.3-s1", null)]
    [InlineData("cr.meshweaver.cloud/plugins/_releases", "cr.meshweaver.cloud", "plugins/_releases", null, null)]
    public void AReferenceParses(string reference, string registry, string repository, string? tag, string? digest)
    {
        OciReference.TryParse(reference, out var parsed).Should().BeTrue();
        parsed!.Registry.Should().Be(registry);
        parsed.Repository.Should().Be(repository);
        parsed.Tag.Should().Be(tag);
        parsed.Digest.Should().Be(digest);
        parsed.ToString().Should().Be(reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-slash")]
    [InlineData("host/")]
    [InlineData("host/repo@sha256:short")]
    [InlineData("host/repo@md5:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void AMalformedReference_DoesNotParse(string reference)
    {
        OciReference.TryParse(reference, out _).Should().BeFalse();
    }
}
