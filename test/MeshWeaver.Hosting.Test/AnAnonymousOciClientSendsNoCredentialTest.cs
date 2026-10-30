using System;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <see cref="OciRegistryClient.Anonymous"/> — the credential-free path a registry DECLARED public
/// is read through (MeshWeaver.Deploy#65: a local install in registry mode reads its platform
/// images from the public <c>ghcr.io/systemorph/memex-portal-ai</c> and holds no credential that
/// registry could check).
///
/// <para>The fake substitutes the NETWORK, never a MeshWeaver interface, and speaks GHCR's public
/// shape: every <c>/v2</c> read without a bearer is a <c>401</c> Bearer challenge naming a realm on
/// the registry's own host; the realm issues a pull bearer to a request carrying NO
/// <c>Authorization</c> header; the tags listing pages at two, continued by a relative
/// <c>Link</c>. Every header the fake sees is recorded BEFORE it decides what to answer, so the
/// "no credential was sent" assertions can fail.</para>
///
/// <para>Negative control: the KEYED client against the same fake presents <c>Basic</c> at the
/// realm — the fake sees it — so the anonymous assertion is not vacuous; and a realm that refuses
/// anonymous callers (a private repository) faults by name rather than answering empty.</para>
/// </summary>
public class AnAnonymousOciClientSendsNoCredentialTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string Host = "public-registry.example.test";
    private const string Repository = "systemorph/memex-portal-ai";

    private static readonly string[] Tags = ["3.1.11805", "3.1.12035", "3.1.12090", "latest", "latest-linux-arm64"];

    private readonly FakePublicRegistry registry = new();

    private static TimeSpan Budget => TestTimeouts.Quick;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(new FakeFactory(registry)));

    [Fact]
    public async Task AnAnonymousClientListsEveryPage_AndNeverSendsACredential()
    {
        var ct = TestContext.Current.CancellationToken;

        var tags = await OciRegistryClient.Anonymous(Mesh, Host)
            .ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct);

        Assert.Equal(Tags, tags);
        Assert.True(registry.PagesServed > 1,
            "the fixture pages at two, so a lister that stopped after the first page would miss the newest tags");
        Assert.Equal(1, registry.TokenRequests);
        // 🚨 The property the public path exists for: nothing but the realm's own anonymous bearer
        // was ever presented, and the realm request carried no Authorization header at all.
        Assert.Empty(registry.AuthorizationsSeen.Where(a => !a.StartsWith("Bearer ", StringComparison.Ordinal)));
        Assert.Equal(0, registry.TokenRequestsWithAuthorization);
    }

    [Fact]
    public async Task NegativeControl_TheKeyedClientPresentsBasic_SoTheFixtureSeesACredential()
    {
        var ct = TestContext.Current.CancellationToken;

        var tags = await new OciRegistryClient(Mesh, Host, Observable.Return("mwi_a-key-the-public-host-must-never-see"))
            .ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct);

        Assert.Equal(Tags, tags);
        Assert.Contains(registry.AuthorizationsSeen, a => a.StartsWith("Basic ", StringComparison.Ordinal));
        Assert.Equal(1, registry.TokenRequestsWithAuthorization);
    }

    [Fact]
    public async Task ARealmThatRefusesAnonymousCallers_FaultsByName_NeverAnEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        registry.RefuseAnonymous = true;

        var fault = await Assert.ThrowsAsync<RegistryRefusedException>(() =>
            OciRegistryClient.Anonymous(Mesh, Host).ListTags(Repository).FirstAsync().Timeout(Budget).Await(ct));

        Assert.Contains("ANONYMOUS", fault.Message);
        Assert.Contains(Repository, fault.Message);
        Assert.Contains("No credential was sent", fault.Message);
        Assert.Equal(0, registry.TokenRequestsWithAuthorization);
    }

    /// <summary>GHCR's public pull surface, in process.</summary>
    private sealed class FakePublicRegistry : HttpMessageHandler
    {
        private const string AnonymousBearer = "anonymous-pull-bearer";
        private const int PageSize = 2;

        private ImmutableList<string> authorizationsSeen = ImmutableList<string>.Empty;
        private int tokenRequests;
        private int tokenRequestsWithAuthorization;
        private int pagesServed;

        public bool RefuseAnonymous { get; set; }
        public ImmutableList<string> AuthorizationsSeen => authorizationsSeen;
        public int TokenRequests => tokenRequests;
        public int TokenRequestsWithAuthorization => tokenRequestsWithAuthorization;
        public int PagesServed => pagesServed;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Headers.Authorization is { } offered)
                ImmutableInterlocked.Update(ref authorizationsSeen, a => a.Add($"{offered.Scheme} {offered.Parameter}"));
            if (!string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (uri.AbsolutePath == "/token")
            {
                Interlocked.Increment(ref tokenRequests);
                if (request.Headers.Authorization is not null)
                {
                    // A real public realm would happily mint a bearer for a presented credential
                    // too — the point is that it RECEIVED one. Counted, then served.
                    Interlocked.Increment(ref tokenRequestsWithAuthorization);
                    return Task.FromResult(Json($$"""{"token":"{{AnonymousBearer}}"}"""));
                }
                return Task.FromResult(RefuseAnonymous
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : Json($$"""{"token":"{{AnonymousBearer}}"}"""));
            }

            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: AnonymousBearer })
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer",
                    $"realm=\"https://{Host}/token\",service=\"{Host}\",scope=\"repository:{Repository}:pull\""));
                return Task.FromResult(challenge);
            }

            if (uri.AbsolutePath == $"/v2/{Repository}/tags/list")
            {
                Interlocked.Increment(ref pagesServed);
                var last = System.Web.HttpUtility.ParseQueryString(uri.Query)["last"];
                var remaining = last is null ? Tags : Tags.SkipWhile(t => t != last).Skip(1).ToArray();
                var page = remaining.Take(PageSize).ToArray();
                var response = Json(
                    $$"""{"name":"{{Repository}}","tags":[{{string.Join(",", page.Select(t => $"\"{t}\""))}}]}""");
                if (page.Length < remaining.Length)
                    response.Headers.TryAddWithoutValidation(
                        "Link", $"</v2/{Repository}/tags/list?last={page[^1]}&n={PageSize}>; rel=\"next\"");
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
