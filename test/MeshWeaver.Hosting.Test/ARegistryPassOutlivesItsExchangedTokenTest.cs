using System;
using System.Collections.Immutable;
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
using MeshWeaver.Mesh.Security;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 Systemorph/MeshWeaver#7790 — a registry pass must outlive the token it started with.
///
/// <para><b>The measured case.</b> memex.systemorph.com's boot install and its bundle adoption were
/// refused <c>401 "A registered instance key is required"</c> by the registry for DataModelling,
/// Developer and, on <c>/health</c>, 13–17 of 40 bundle fetches — while the same installation's
/// instance key authenticated at the registry minutes later (its record's <c>lastSeenAt</c> kept
/// advancing). The refused fetches were the LATER ones of a pass. Since #7312 a configured
/// <c>mwi_</c> key is exchanged for a short-lived sync token too, and every registry client of a
/// pass was built ONCE from one resolved token string: a pass that outlived the token (at most an
/// hour, as little as <see cref="RegistryTokenResolver.RefreshMargin"/> when it came from the
/// cache) presented an expired token on every remaining request, and the registry's definitive
/// refusal of an expired token is that 401.</para>
///
/// <para><b>The rule pinned here.</b> A client built from <see cref="RegistryTokenResolver.Credential"/>
/// reads the credential per request, so a request after the token expired presents a freshly
/// exchanged one. The control: a client built from the resolved STRING — the shape every pass used
/// — is refused 401 on that same request. Real resolver, real clients, real HTTP stack; only the
/// registry is a fake handler that issues tokens and refuses the ones it has expired.</para>
/// </summary>
public class ARegistryPassOutlivesItsExchangedTokenTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string InstanceKey = "mwi_registered-instance-key";

    private readonly FakeRegistry registry = new();

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddPluginCatalog()
            .ConfigureServices(services => services
                .AddSingleton(new PluginCatalogOptions { InstallPreInstalledPackages = false })
                .AddSingleton<IHttpClientFactory>(new FakeFactory(registry)));

    /// <summary>A CONFIGURED instance key — exchanged since #7312, which is what memex runs on.</summary>
    private static PluginRegistryReference Reference =>
        new() { Name = "fake", Url = FakeRegistry.Url, Token = InstanceKey };

    private static readonly PackageManifest DataModelling = new() { Id = "DataModelling", Name = "DataModelling", Version = "1.0.0" };
    private static readonly PackageManifest Developer = new() { Id = "Developer", Name = "Developer", Version = "1.0.0" };

    private RegistryTokenResolver Resolver => Mesh.ServiceProvider.GetRequiredService<RegistryTokenResolver>();

    /// <summary>
    /// SHOULD-FAIL-IF: the package source holds the token it was built with. Then the second fetch,
    /// after the registry expired that token, is refused 401 exactly like Developer on memex.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AFileFetchAfterTheTokenExpired_PresentsAFreshToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new RegistryPackageSource(Mesh, FakeRegistry.Url, Resolver.Credential(Reference));

        var first = await source.FetchPackage(DataModelling, "HEAD", null)
            .Should().Within(TestTimeouts.Convergence).Emit("the first fetch of the pass is served", ct);
        first.Files.Should().ContainSingle();

        registry.ExpireIssuedTokens();

        var second = await source.FetchPackage(Developer, "HEAD", null)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a fetch after the token expired presents a freshly exchanged token and is served", ct);
        second.Files.Should().ContainSingle();
        registry.Exchanges.Should().Be(2, "the expired token was replaced by exchanging the instance key again");
        registry.Refusals.Should().Be(0, "no request carried an expired token");
    }

    /// <summary>
    /// The bundle half (<c>bundle_adoption</c>'s <c>FetchFailed (HTTP 401 …)</c>): the download
    /// route of a client built from the credential source is served after the token expired.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABundleDownloadAfterTheTokenExpired_PresentsAFreshToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var bundles = new PluginBundleClient(Mesh, FakeRegistry.Url, Resolver.Credential(Reference));

        var first = await bundles.DownloadOverHttp("Cursor", "1.0.0")
            .Should().Within(TestTimeouts.Convergence).Emit("the first download answers", ct);
        first.Kind.Should().Be(BundleAdoptionKind.Adopted, first.Reason ?? "served while the token is live");

        registry.ExpireIssuedTokens();

        var second = await bundles.DownloadOverHttp("DataModelling", "1.0.0")
            .Should().Within(TestTimeouts.Convergence).Emit("the later download answers", ct);
        second.Kind.Should().Be(BundleAdoptionKind.Adopted,
            $"a download after the token expired presents a fresh one (got: {second.Reason})");
        registry.Refusals.Should().Be(0);
    }

    /// <summary>
    /// CONTROL — the shape every pass used before the fix: the clients built from ONE resolved
    /// token string. The same sequence is refused 401 after expiry, by both clients, which is the
    /// production symptom; it proves the fake registry's expiry is what the fix answers.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Control_AClientBuiltFromOneResolvedToken_IsRefusedOnceItExpires()
    {
        var ct = TestContext.Current.CancellationToken;
        var token = await Resolver.ResolveToken(Reference).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit("the token resolves", ct);
        token.Should().StartWith(FakeRegistry.TokenPrefix, "the configured instance key is exchanged (#7312)");
        var source = new RegistryPackageSource(Mesh, FakeRegistry.Url, token);
        var bundles = new PluginBundleClient(Mesh, FakeRegistry.Url, token);

        (await source.FetchPackage(DataModelling, "HEAD", null)
                .Should().Within(TestTimeouts.Convergence).Emit("served while the token is live", ct))
            .Files.Should().ContainSingle();

        registry.ExpireIssuedTokens();

        var refused = await source.FetchPackage(Developer, "HEAD", null)
            .Select(_ => (Exception?)null)
            .Catch((Exception ex) => Observable.Return<Exception?>(ex))
            .Should().Within(TestTimeouts.Convergence).Emit("the fetch answers", ct);
        refused.Should().BeOfType<RegistryResponseException>()
            .Which.Message.Should().Contain("401").And.Contain("A registered instance key is required");

        var missed = await bundles.DownloadOverHttp("DataModelling", "1.0.0")
            .Should().Within(TestTimeouts.Convergence).Emit("the download answers", ct);
        missed.Kind.Should().Be(BundleAdoptionKind.FetchFailed);
        missed.Reason.Should().Contain("HTTP 401");
    }

    /// <summary>
    /// The registry: <c>/api/instances/token</c> exchanges the instance key for an opaque token; the
    /// file and bundle routes serve a LIVE token and refuse anything else with the registry's own
    /// 401 body. Each token is issued with <see cref="RegistryTokenResolver.RefreshMargin"/> of
    /// life — a token already inside its refresh margin, as one taken from the cache late in its
    /// life is — so the resolver re-exchanges on every resolve and the test needs no clock.
    /// </summary>
    private sealed class FakeRegistry : HttpMessageHandler
    {
        public const string Url = "https://registry.test";
        public const string TokenPrefix = "mwa_fake-";

        private ImmutableHashSet<string> live = ImmutableHashSet<string>.Empty;
        private int exchanges;
        private int refusals;

        public int Exchanges => Volatile.Read(ref exchanges);

        public int Refusals => Volatile.Read(ref refusals);

        /// <summary>Every token issued so far stops verifying — what its <c>exp</c> does.</summary>
        public void ExpireIssuedTokens() => ImmutableInterlocked.Update(ref live, _ => ImmutableHashSet<string>.Empty);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var presented = request.Headers.Authorization?.Parameter ?? "";

            if (path == SyncTokenPayloads.Route)
            {
                if (presented != InstanceKey)
                    return Task.FromResult(Refuse());
                var token = TokenPrefix + Interlocked.Increment(ref exchanges);
                ImmutableInterlocked.Update(ref live, set => set.Add(token));
                var body = JsonSerializer.Serialize(
                    new SyncTokenPayloads.Response(token, "Bearer",
                        (int)RegistryTokenResolver.RefreshMargin.TotalSeconds, []),
                    SyncTokenPayloads.Json);
                return Task.FromResult(Json(body));
            }

            if (!live.Contains(presented))
            {
                Interlocked.Increment(ref refusals);
                return Task.FromResult(Refuse());
            }

            if (path == RegistryPackageSource.RoutePrefix + "/files")
                return Task.FromResult(Json(PluginRegistryPayloads.Files([new PackageFile("index.json", "{}")])));
            if (path.StartsWith(PluginBundleClient.RoutePrefix + "/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3]),
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Refuse() => new(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"error\":\"A registered instance key is required (Authorization: Bearer mwi_…).\"}",
                Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
