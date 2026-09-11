using System.Net;
using Memex.Portal.Shared.Api;
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
/// Pins the two-host rule (<see cref="PublicSite"/>): with a public host configured, the app host
/// sends a stranger's request for a public page to the public host with a permanent redirect,
/// declares itself off-limits to crawlers, and every absolute crawler-facing URL names the public
/// host — while a deployment with ONE host behaves exactly as before. Also pins that <c>HEAD</c>
/// is answered (it was a 405 from every page, measured 2026-09-11).
/// </summary>
public class PublicSiteTest
{
    private const string Public = "www.example.test";
    private const string App = "memex.example.test";

    private static IConfiguration Config(string? publicHost, string? landing = null)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PublicSite.PublicHostKey] = publicHost,
            [PublicSite.LandingPathKey] = landing,
        }).Build();

    private static HttpRequest RequestOn(string host, string path = "/")
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString(host);
        http.Request.Path = path;
        return http.Request;
    }

    [Fact]
    public void WithNoPublicHost_TheRequestHostIsCanonical_AndNothingIsAppOnly()
    {
        var configuration = Config(null);
        Assert.Equal("https://memex.example.test", PublicSite.CanonicalBaseUrl(configuration, RequestOn(App)));
        Assert.False(PublicSite.IsAppOnlyHost(configuration, RequestOn(App)));
    }

    [Fact]
    public void WithAPublicHost_ItIsCanonicalFromEveryHost_AndTheOtherHostIsAppOnly()
    {
        var configuration = Config(Public);
        Assert.Equal("https://www.example.test", PublicSite.CanonicalBaseUrl(configuration, RequestOn(App)));
        Assert.Equal("https://www.example.test", PublicSite.CanonicalBaseUrl(configuration, RequestOn(Public)));
        Assert.True(PublicSite.IsAppOnlyHost(configuration, RequestOn(App)));
        Assert.False(PublicSite.IsAppOnlyHost(configuration, RequestOn("WWW.EXAMPLE.TEST")));
    }

    [Fact]
    public void TheRootIsTheLandingNode_OnlyWhenOneIsConfigured()
    {
        Assert.Equal("MeshWeaver", PublicSite.NodePathFor(Config(Public, "/MeshWeaver/"), "/"));
        Assert.Equal("", PublicSite.NodePathFor(Config(Public), "/"));
        Assert.Equal("Doc/Architecture", PublicSite.NodePathFor(Config(Public, "MeshWeaver"), "/Doc/Architecture/"));
    }

    /// <summary>
    /// The pipeline under test: the redirect and HEAD middleware over a TestServer, a stub
    /// publicness decision (a real one asks the mesh through the anonymous gate), and one ordinary
    /// page route so "not redirected" is proved against something that answers.
    /// </summary>
    private static WebApplication Host(string? publicHost, string? landing = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PublicSite.PublicHostKey] = publicHost,
            [PublicSite.LandingPathKey] = landing,
        });
        var app = builder.Build();
        // HEAD→GET must run BEFORE routing: an endpoint is matched by method, and a GET-only page
        // route never matches HEAD. Explicit UseRouting here is what the portal's pipeline has;
        // without it WebApplication would insert routing ahead of every middleware.
        app.UseHeadAsGet();
        app.UseRouting();
        app.UsePublicHostRedirect((_, nodePath) =>
            Task.FromResult(nodePath is "PublicSpace" or "PublicSpace/Guide" or "Landing"));
        app.MapSeo();
        app.MapGet("/{**path}", (string? path) => Results.Text($"page:{path}", "text/html"));
        app.Start();
        return app;
    }

    private static HttpRequestMessage On(string host, string path, string method = "GET", string accept = "text/html")
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("Accept", accept);
        return request;
    }

    [Fact]
    public async Task AStrangerAskingTheAppHostForAPublicPage_IsSentToThePublicHost_Permanently()
    {
        using var app = Host(Public);
        var client = app.GetTestClient();
        var response = await client.SendAsync(On(App, "/PublicSpace/Guide?tab=1"));
        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("https://www.example.test/PublicSpace/Guide?tab=1", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task ThePublicHostServesThePage_AndNeverRedirectsToItself()
    {
        using var app = Host(Public);
        var response = await app.GetTestClient().SendAsync(On(Public, "/PublicSpace/Guide"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("page:PublicSpace/Guide", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task APrivatePage_AnApiRoute_AndANonHtmlRequest_StayOnTheAppHost()
    {
        using var app = Host(Public);
        var client = app.GetTestClient();
        // Private: the decision says no → the app's own handling (sign-in) answers, no redirect.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(On(App, "/PrivateSpace"))).StatusCode);
        // Not a node path.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(On(App, "/api/version"))).StatusCode);
        // A JSON client is never bounced between hosts.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(On(App, "/PublicSpace", accept: "application/json"))).StatusCode);
    }

    [Fact]
    public async Task WithOneHost_NothingRedirects()
    {
        using var app = Host(publicHost: null);
        var response = await app.GetTestClient().SendAsync(On(App, "/PublicSpace"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheRootOnTheAppHost_IsTheLandingPage_AndRedirectsLikeOne()
    {
        using var app = Host(Public, landing: "Landing");
        var response = await app.GetTestClient().SendAsync(On(App, "/"));
        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("https://www.example.test/", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task RobotsOnTheAppHost_DisallowsEverything_AndPointsAtThePublicSitemap()
    {
        using var app = Host(Public);
        var body = await (await app.GetTestClient().SendAsync(On(App, "/robots.txt", accept: "*/*"))).Content.ReadAsStringAsync();
        Assert.Contains("Disallow: /\n", body.Replace("\r\n", "\n") + "\n");
        Assert.Contains("Sitemap: https://www.example.test/sitemap.xml", body);
        Assert.DoesNotContain("Disallow: /login", body);
    }

    [Fact]
    public async Task RobotsOnThePublicHost_KeepsThePageSurfaceOpen()
    {
        using var app = Host(Public);
        var body = await (await app.GetTestClient().SendAsync(On(Public, "/robots.txt", accept: "*/*"))).Content.ReadAsStringAsync();
        Assert.Contains("Disallow: /login", body);
        Assert.Contains("Disallow: /welcome", body);
        Assert.Contains("Disallow: /api/", body);
        Assert.DoesNotContain("Disallow: /\n", body.Replace("\r\n", "\n"));
        Assert.Contains("Sitemap: https://www.example.test/sitemap.xml", body);
    }

    [Fact]
    public async Task Head_IsAnsweredLikeGet_WithNoBody()
    {
        using var app = Host(publicHost: null);
        var response = await app.GetTestClient().SendAsync(On(App, "/Doc", method: "HEAD"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }
}
