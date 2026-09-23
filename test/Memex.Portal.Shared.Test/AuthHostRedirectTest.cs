using System.Net;
using Memex.Portal.Shared.Seo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the sign-in host rule (<see cref="PublicSite.UseAuthHostRedirect"/>): a sign-in that STARTS
/// on a brand host is sent to the host that owns sign-in, so the OAuth <c>redirect_uri</c> always
/// names the host the identity providers were registered with.
///
/// <para>The defect this prevents, measured 2026-09-23 on <c>www.meshweaver.cloud</c>: all three
/// providers were handed their own brand host — <c>redirect_uri=https://www.meshweaver.cloud/signin-microsoft</c>
/// and the <c>-google</c> / <c>-linkedin</c> equivalents — while the registered value was the app
/// host, so LinkedIn answered "The redirect_uri does not match the registered value" and Google
/// "Access blocked: This app's request is invalid".</para>
/// </summary>
public class AuthHostRedirectTest
{
    private const string Auth = "memex.example.test";
    private const string Brand = "www.example.test";

    private static IConfiguration Config(string? authHost)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PublicSite.AuthHostKey] = authHost,
        }).Build();

    private static HttpRequest RequestOn(string host, string path, string query = "")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString(host);
        http.Request.Path = path;
        if (query.Length > 0) http.Request.QueryString = new QueryString(query);
        return http.Request;
    }

    // ───────────────────────────── the pure rule ─────────────────────────────

    [Fact]
    public void SignInStartingOnABrandHost_IsOnTheWrongHost()
        => Assert.True(PublicSite.StartsSignInOnWrongHost(
            Config(Auth), RequestOn(Brand, "/auth/login", "?provider=Microsoft")));

    [Fact]
    public void SignInStartingOnTheAuthHost_IsNotRedirected()
        => Assert.False(PublicSite.StartsSignInOnWrongHost(
            Config(Auth), RequestOn(Auth, "/auth/login", "?provider=Microsoft")));

    /// <summary>
    /// 🚨 The CALLBACK must never move hosts: it carries the correlation and nonce cookies the
    /// challenge set, which are scoped to the issuing host.
    /// </summary>
    [Theory]
    [InlineData("/signin-microsoft")]
    [InlineData("/signin-google")]
    [InlineData("/signin-linkedin")]
    [InlineData("/signin-apple")]
    public void ACallbackIsNeverRedirected_EvenOnABrandHost(string callback)
        => Assert.False(PublicSite.StartsSignInOnWrongHost(Config(Auth), RequestOn(Brand, callback)));

    /// <summary>A deployment with one sign-in host is untouched — memex.systemorph.com's shape.</summary>
    [Fact]
    public void WithNoAuthHostConfigured_NothingIsRedirected()
        => Assert.False(PublicSite.StartsSignInOnWrongHost(
            Config(null), RequestOn(Brand, "/auth/login", "?provider=Microsoft")));

    [Fact]
    public void AnOrdinaryPageOnABrandHost_IsNotASignIn()
        => Assert.False(PublicSite.StartsSignInOnWrongHost(Config(Auth), RequestOn(Brand, "/Doc/Architecture")));

    // ───────────────────────────── the middleware ─────────────────────────────

    /// <summary>
    /// The pipeline under test: the auth-host redirect over a TestServer, with one ordinary route
    /// behind it so "not redirected" is proved against something that actually answers. Built the
    /// way <c>PublicSiteTest</c> builds its host, so both two-host rules are pinned the same way.
    /// </summary>
    private static WebApplication Host(string? authHost)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PublicSite.AuthHostKey] = authHost,
        });
        var app = builder.Build();
        app.UseAuthHostRedirect();
        app.MapGet("/{**path}", () => Results.Text("endpoint reached", "text/plain"));
        app.Start();
        return app;
    }

    private static HttpRequestMessage On(string host, string pathAndQuery)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        request.Headers.Host = host;
        return request;
    }

    private static async Task<HttpResponseMessage> Send(string? authHost, string host, string pathAndQuery)
    {
        using var app = Host(authHost);
        return await app.GetTestClient().SendAsync(On(host, pathAndQuery));
    }

    [Fact]
    public async Task ABrandHostSignIn_IsSentToTheAuthHost_WithItsQueryIntact()
    {
        var response = await Send(Auth, Brand, "/auth/login?provider=Microsoft&returnUrl=%2FDoc");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            $"https://{Auth}/auth/login?provider=Microsoft&returnUrl=%2FDoc",
            response.Headers.Location!.ToString());
    }

    /// <summary>
    /// 🚨 302, never 301. The sign-in host is configuration and may move; a browser caches a
    /// permanent redirect past the deploy that changes it.
    /// </summary>
    [Fact]
    public async Task TheRedirectIsTemporary_SoTheAuthHostCanMove()
    {
        var response = await Send(Auth, Brand, "/auth/login?provider=Google");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.MovedPermanently, response.StatusCode);
    }

    [Fact]
    public async Task OnTheAuthHost_TheRequestReachesTheEndpoint()
    {
        var response = await Send(Auth, Auth, "/auth/login?provider=Microsoft");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("endpoint reached", await response.Content.ReadAsStringAsync());
    }

    /// <summary>The NEGATIVE CONTROL: with no auth host, the same brand-host request is served.</summary>
    [Fact]
    public async Task WithNoAuthHost_TheBrandHostServesSignInItself()
    {
        var response = await Send(null, Brand, "/auth/login?provider=Microsoft");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("endpoint reached", await response.Content.ReadAsStringAsync());
    }
}
