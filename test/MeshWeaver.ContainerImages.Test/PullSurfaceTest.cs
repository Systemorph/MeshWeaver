using System.Net;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.ContainerImages.Test;

/// <summary>
/// The pull surface driven over a REAL HTTP pipeline, against a real in-process upstream registry.
///
/// <para>The subject is the CONVERSATION, not the individual handlers: an OCI client probes
/// <c>/v2/</c>, reads a challenge, fetches the realm the challenge names, and only then pulls. Every
/// step of that has to line up, and the step that did not was the realm — the challenge named
/// <c>/v2/token</c> and nothing served it, so a client's dance went 401 → 404 → give up. Nothing
/// short of walking the whole handshake catches that: each endpoint in isolation looked right.</para>
/// </summary>
public class PullSurfaceTest
{
    private const string InstanceKey = "mw_instance_key";
    private const string CallerName = "instance/ci";

    /// <summary>
    /// The reference implementation of the seam: ONE shape (<c>Bearer &lt;instance key&gt;</c>),
    /// because the mirror normalises before it asks. Real, not a mock — the contract is small
    /// enough that a stand-in with different behaviour would be testing the stand-in.
    /// </summary>
    private sealed class InstanceKeyAuthenticator : IContainerImageAuthenticator
    {
        public IObservable<string?> Authenticate(string? authorizationHeader, CancellationToken ct) =>
            Observable.Return(
                RegistryCredential.TryReadSecret(authorizationHeader) == InstanceKey
                    ? CallerName
                    : null);
    }

    private static async Task<WebApplication> BuildMirror(
        FakeUpstreamRegistry upstream,
        bool configured = true,
        string? imageRoot = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var options = new ContainerImageOptions
        {
            Upstream = configured ? FakeUpstreamRegistry.Host : null,
            Username = configured ? FakeUpstreamRegistry.Username : null,
            Password = configured ? FakeUpstreamRegistry.Password : null,
            Repositories = [FakeUpstreamRegistry.Repository],
            ImageRoot = imageRoot,
        };
        builder.Services.AddSingleton(Options.Create(options));
        // 🚨 A SINGLETON, as the portal must register it: the upstream token cache is an INSTANCE
        // field on this object, so a transient registration would silently re-fetch a token per
        // request — which `OneTokenExchangeServesManyPulls` below is the guard for.
        builder.Services.AddSingleton(_ => new HttpClient(upstream));
        builder.Services.AddSingleton<UpstreamRegistryClient>();
        builder.Services.AddSingleton<IContainerImageAuthenticator, InstanceKeyAuthenticator>();

        var app = builder.Build();
        app.MapContainerImages();
        // The negative control: without a route that DOES answer, "everything 404s" and "nothing
        // is mapped" would be the same observation.
        app.MapGet("/ordinary", () => "the portal");
        // StartAsync, not Start(): the sync overload blocks the calling thread on host startup.
        await app.StartAsync();
        return app;
    }

    private static HttpClient Anonymous(WebApplication app) => app.GetTestClient();

    private static HttpClient Authenticated(WebApplication app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", InstanceKey);
        return client;
    }

    // ── the handshake ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE WHOLE DANCE, as a real client walks it: probe → challenge → token → pull. The realm
    /// the challenge names must be a route that answers, or every <c>docker pull</c> ends at a 404
    /// it cannot interpret. This is the test that proves the handshake COMPLETES; asserting the
    /// challenge header alone proved only that a client would be sent somewhere.
    /// </summary>
    [Fact]
    public async Task TheBearerDanceCompletes_ProbeThenChallengeThenTokenThenPull()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);
        var client = Anonymous(app);

        // 1. The probe, unauthenticated: 401 carrying a challenge that names a realm.
        var probe = await client.GetAsync("/v2/");
        Assert.Equal(HttpStatusCode.Unauthorized, probe.StatusCode);
        var challenge = Assert.Single(probe.Headers.WwwAuthenticate).ToString();
        Assert.StartsWith("Bearer ", challenge);
        Assert.Equal("registry/2.0", probe.Headers.GetValues("Docker-Distribution-Api-Version").Single());

        var realm = Realm(challenge);
        Assert.Contains("/v2/token", realm);

        // 2. The realm, with the credential a `docker login` stores — Basic, not Bearer.
        var tokenRequest = new HttpRequestMessage(
            HttpMethod.Get, $"{realm}?service=localhost&scope=repository:{FakeUpstreamRegistry.Repository}:pull");
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"ci:{InstanceKey}")));
        var tokenResponse = await client.SendAsync(tokenRequest);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);

        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var bearer = token.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(bearer));
        // Both spellings, because clients read one or the other.
        Assert.Equal(bearer, token.RootElement.GetProperty("access_token").GetString());
        Assert.True(token.RootElement.GetProperty("expires_in").GetInt32() > 0);

        // 3. The token the exchange handed back opens the probe…
        var authorized = new HttpRequestMessage(HttpMethod.Get, "/v2/");
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(authorized)).StatusCode);

        // 4. …and a manifest pull.
        var pull = new HttpRequestMessage(
            HttpMethod.Get, $"/v2/{FakeUpstreamRegistry.Repository}/manifests/ci.7794");
        pull.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        var manifest = await client.SendAsync(pull);
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal(
            FakeUpstreamRegistry.ManifestJson.Trim(),
            (await manifest.Content.ReadAsStringAsync()).Trim());
    }

    /// <summary>
    /// The challenge names the SCOPE for the repository actually requested — the shape ACR emits,
    /// and what a client copies verbatim into its token request.
    /// </summary>
    [Fact]
    public async Task TheChallengeOnARepositoryRouteNamesThatRepositorysPullScope()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);

        var response = await Anonymous(app)
            .GetAsync($"/v2/{FakeUpstreamRegistry.Repository}/manifests/ci.7794");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            $"scope=\"repository:{FakeUpstreamRegistry.Repository}:pull\"",
            Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    /// <summary>
    /// 🚨 The token endpoint refuses with a BARE 401 — no challenge. Challenging here would name
    /// this very route, and a client that follows challenges would loop between the two forever.
    /// </summary>
    [Fact]
    public async Task ARefusedTokenExchangeCarriesNoChallenge_SoAClientCannotLoop()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);

        var anonymous = await Anonymous(app).GetAsync("/v2/token");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(anonymous.Headers.WwwAuthenticate);

        var wrongKey = app.GetTestClient();
        wrongKey.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-the-key");
        var refused = await wrongKey.GetAsync("/v2/token");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Empty(refused.Headers.WwwAuthenticate);
    }

    /// <summary>
    /// Route precedence, asserted rather than assumed: the literal <c>/v2/token</c> must win over
    /// the <c>/v2/{**rest}</c> catch-all, or the token endpoint would be swallowed by the pull
    /// handler (which would parse it as a route, fail, and 404).
    /// </summary>
    [Fact]
    public async Task TheTokenRouteWinsOverThePullCatchAll()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);

        var response = await Authenticated(app).GetAsync("/v2/token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("access_token", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The token IS the caller's instance key, deliberately — the mirror mints nothing. So
    /// revoking the key revokes the pull at the next exchange, instead of leaving a minted
    /// credential valid for its full lifetime.
    /// </summary>
    [Fact]
    public async Task TheIssuedTokenIsTheCallersOwnKey_SoRevocationTakesEffect()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);

        var response = await Authenticated(app).GetAsync("/v2/token");
        using var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(InstanceKey, token.RootElement.GetProperty("token").GetString());
    }

    // ── the pull routes ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TagsListIsServed()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);

        var response = await Authenticated(app)
            .GetAsync($"/v2/{FakeUpstreamRegistry.Repository}/tags/list");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ci.7794", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ABlobIsServedWhole_AndByteForByte()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);

        var response = await Authenticated(app).GetAsync(
            $"/v2/{FakeUpstreamRegistry.Repository}/blobs/sha256:a000000000000000000000000000000000000000000000000000000000000000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FakeUpstreamRegistry.LayerBytes, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// Range requests are not optional: a client resuming an interrupted layer pull asks for one,
    /// and a mirror that answered 200-with-everything would restart the transfer it was asked to
    /// continue.
    /// </summary>
    [Fact]
    public async Task ARangeRequestIsForwarded_AndPartialContentComesBack()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);
        var client = Authenticated(app);
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/v2/{FakeUpstreamRegistry.Repository}/blobs/sha256:a000000000000000000000000000000000000000000000000000000000000000");
        request.Headers.Range = new RangeHeaderValue(100, 199);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(100, body.Length);
        Assert.Equal(FakeUpstreamRegistry.LayerBytes[100..200], body);
        // Content-Range is a CONTENT header on the client side, whatever the wire looks like.
        Assert.Equal("bytes 100-199/1048576",
            response.Content.Headers.GetValues("Content-Range").Single());
    }

    /// <summary>
    /// 🚨 THE STREAMING PROOF. The upstream hands over its first chunk and then PARKS. If the
    /// mirror buffered the layer, the client could not possibly hold those bytes while the
    /// upstream still holds the rest — so receiving them IS the evidence that nothing is
    /// materialised. A portal that buffers a 300 MB layer OOMs under a rolling restart.
    /// </summary>
    [Fact]
    public async Task ALayerStreams_TheClientGetsBytesWhileTheUpstreamIsStillProducing()
    {
        var upstream = new FakeUpstreamRegistry { ParkLayerAfterFirstChunk = true };
        using var app = await BuildMirror(upstream);
        try
        {
            var response = await Authenticated(app).GetAsync(
                $"/v2/{FakeUpstreamRegistry.Repository}/blobs/sha256:a000000000000000000000000000000000000000000000000000000000000000",
                HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var body = await response.Content.ReadAsStreamAsync();
            var head = new byte[1024];
            var read = await body.ReadAtLeastAsync(head, 1024, throwOnEndOfStream: false);

            Assert.Equal(1024, read);
            Assert.Equal(FakeUpstreamRegistry.LayerBytes[..1024], head);
        }
        finally
        {
            // In a finally: a failing assertion above must not strand the parked producer into
            // the next test.
            Volatile.Write(ref upstream.ReleaseLayer, 1);
        }
    }

    // ── the refusals ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unconfigured is 404 on EVERY route, never a partial service — a half-configured registry
    /// that served something would be indistinguishable from a working one until a pull returned
    /// the wrong bytes.
    /// </summary>
    [Theory]
    [InlineData("/v2/")]
    [InlineData("/v2/memex-portal-ai/manifests/ci.7794")]
    [InlineData("/v2/memex-portal-ai/tags/list")]
    [InlineData("/v2/token")]
    public async Task AnUnconfiguredMirrorIs404Everywhere(string path)
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream, configured: false);

        Assert.Equal(HttpStatusCode.NotFound, (await Authenticated(app).GetAsync(path)).StatusCode);
        // The negative control: the host itself is serving.
        Assert.Equal(HttpStatusCode.OK, (await Anonymous(app).GetAsync("/ordinary")).StatusCode);
        // …and nothing reached the upstream.
        Assert.Equal(0, upstream.Requests);
    }

    /// <summary>
    /// A repository the upstream WOULD serve but the allowlist does not name is 404 — and the
    /// request never leaves the portal. Empty means none; one upstream credential must not become
    /// an open read proxy for the whole registry.
    /// </summary>
    [Fact]
    public async Task ARepositoryOutsideTheAllowlistNeverReachesTheUpstream()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);

        var response = await Authenticated(app)
            .GetAsync($"/v2/{FakeUpstreamRegistry.UnlistedRepository}/manifests/ci.7794");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, upstream.Requests);
    }

    /// <summary>The push family, refused by shape at the edge — nothing forwarded.</summary>
    [Theory]
    [InlineData("/v2/memex-portal-ai/blobs/uploads/")]
    [InlineData("/v2/memex-portal-ai/blobs/uploads/abc")]
    [InlineData("/v2/memex-portal-ai/blobs/latest")]
    public async Task UploadRoutesAreRefusedWithoutReachingTheUpstream(string path)
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);

        Assert.Equal(HttpStatusCode.NotFound, (await Authenticated(app).GetAsync(path)).StatusCode);
        Assert.Equal(0, upstream.Requests);
    }

    /// <summary>
    /// 🚨 The MIRROR's own credential failing is a 502, never a 401. A 401 would send the caller
    /// to fix a token that is not the broken thing — and the caller cannot fix ours.
    /// </summary>
    [Fact]
    public async Task TheMirrorsOwnCredentialFailingIsA502()
    {
        var upstream = new FakeUpstreamRegistry { RefuseMirrorCredential = true };
        using var app = await BuildMirror(upstream);

        var response = await Authenticated(app)
            .GetAsync($"/v2/{FakeUpstreamRegistry.Repository}/manifests/ci.7794");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>
    /// The upstream token is cached on the client INSTANCE, so many pulls cost one exchange. The
    /// cache being an instance field on a singleton — never static — is what keeps a credential
    /// from outliving the mesh; this is its observable effect.
    /// </summary>
    [Fact]
    public async Task OneTokenExchangeServesManyPulls()
    {
        var upstream = new FakeUpstreamRegistry();
        using var app = await BuildMirror(upstream);
        var client = Authenticated(app);

        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await client.GetAsync($"/v2/{FakeUpstreamRegistry.Repository}/manifests/ci.7794"))
                .StatusCode);

        Assert.Equal(1, upstream.TokenRequests);
    }

    private static string Realm(string challenge)
    {
        const string marker = "realm=\"";
        var start = challenge.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return challenge[start..challenge.IndexOf('"', start)];
    }
}
