using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using MeshWeaver.Hosting.AspNetCore.Portal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The MCP endpoint routing contract: <c>/api/mcp</c> is the primary published path,
/// <c>/mcp</c> the permanent compatibility alias, and <c>/Mcp</c> — the SAME path family,
/// route matching being case-insensitive — is ALSO the Mcp partition's page (its Store cover).
///
/// <para>This pins the resolution of that collision end-to-end over a real routing pipeline,
/// because it shipped broken in both directions: the blanket "mcp" prefix in
/// <see cref="NonfileRouteConstraint"/> made <c>GET /Mcp</c> answer 404 forever (the partition
/// cover was unreachable on every deployment), while the module-not-loaded failure mode (#2093)
/// needs MCP-protocol traffic to keep answering an honest 404 — never a 200 HTML shell an MCP
/// client cannot parse and an operator reads as healthy.</para>
///
/// <para>The MCP endpoint itself is a stand-in POST route here (the real one is mapped by the
/// MeshWeaver.Mcp module, which lives in the plugins repo) — what is under test is the
/// platform's routing around it: the <see cref="McpEndpointRoutes"/> front-of-pipeline alias,
/// the shape-based page-route constraint, and the RFC 9728 path-inserted discovery rewrite.</para>
/// </summary>
public class McpRoutingTest
{
    private const string McpEndpointBody = "mcp-endpoint";

    /// <summary>
    /// The pipeline under test: the alias startup filter exactly as
    /// <see cref="McpAuthenticationExtensions.AddMcpAuthentication"/> registers it, a literal
    /// POST <c>/mcp</c> endpoint standing in for the module's streamable-HTTP transport
    /// (stateless mode maps no GET — mirrored here, it is what frees browser GETs for the page),
    /// a bare protected-resource-metadata endpoint echoing the stashed resource path, and a
    /// GET+POST catch-all page route carrying the real <see cref="NonfileRouteConstraint"/>
    /// (razor-components page endpoints accept POST for enhanced forms, so the emulation must
    /// too — otherwise the "protocol POST never renders the shell" half would be vacuous).
    /// </summary>
    private static WebApplication BuildApp(bool mapMcpEndpoint)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddTransient<IStartupFilter, McpEndpointRoutes.StartupFilter>();

        var app = builder.Build();

        if (mapMcpEndpoint)
            app.MapPost("/mcp", () => McpEndpointBody);

        app.MapGet(McpEndpointRoutes.ResourceMetadataPath,
            (HttpContext ctx) =>
                ctx.Items.TryGetValue(McpEndpointRoutes.ResourcePathItem, out var stashed)
                && stashed is string resourcePath
                    ? resourcePath
                    : "(bare)");

        app.MapMethods("/{**path}", ["GET", "POST"],
                (string? path) => $"page:{path}")
            .ExcludeStaticAssetPaths();

        return app;
    }

    private static HttpRequestMessage JsonPost(string url) =>
        new(HttpMethod.Post, url)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
                Encoding.UTF8,
                "application/json"),
        };

    [Theory]
    [InlineData("/mcp")]        // the compatibility alias every existing client config uses
    [InlineData("/api/mcp")]    // the primary path — rewritten onto the module's route
    [InlineData("/API/MCP")]    // route matching is case-insensitive; the rewrite must be too
    public async Task JsonPost_ReachesTheMcpEndpoint(string url)
    {
        await using var app = BuildApp(mapMcpEndpoint: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.SendAsync(JsonPost(url));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(McpEndpointBody);
    }

    [Fact]
    public async Task BrowserGetOfMcp_RendersThePartitionPage()
    {
        await using var app = BuildApp(mapMcpEndpoint: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/Mcp");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("page:Mcp",
            because: "a browser navigation to /Mcp must render the Mcp partition's cover, "
                     + "not fall into the endpoint-or-404 hole that shipped");
    }

    [Theory]
    [InlineData("text/event-stream")]                    // streamable-HTTP SSE channel probe
    [InlineData("application/json, text/event-stream")]  // API-shaped read
    public async Task ProtocolShapedGet_NeverGetsTheHtmlShell(string accept)
    {
        await using var app = BuildApp(mapMcpEndpoint: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.TryAddWithoutValidation("Accept", accept);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// An UNMAPPED /api/… path must be a 404, never the app shell. Every API client — curl in a CI
    /// gate, a plugin fetcher, a webhook sender — reads a 200 as success and then parses HTML as
    /// data. Measured 2026-08-27: the plugin gate's upstream-seed fetch took memex's HTML for a
    /// sealed-but-empty publication index because the registry was one build behind #2487.
    /// Mapped API endpoints are untouched (they outrank the catch-all); a partition page still renders.
    /// </summary>
    [Fact]
    public async Task UnknownApiPath_AnswersAnHonest404_NeverTheShell()
    {
        await using var app = BuildApp(mapMcpEndpoint: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        foreach (var accept in new[] { "application/json", "text/html,application/xhtml+xml" })
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/plugins/bundles/prebuilt/s0/plugins");
            request.Headers.TryAddWithoutValidation("Accept", accept);
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound,
                because: $"an unmapped /api path with Accept '{accept}' must fail loud, not render the shell");
        }

        // Control: the exclusion is root-anchored on the `api` segment only — a partition named
        // anything else still reaches the page, and a mesh path merely CONTAINING "api" is untouched.
        foreach (var path in new[] { "/Apiary", "/Docs/api" })
        {
            var browser = new HttpRequestMessage(HttpMethod.Get, path);
            browser.Headers.TryAddWithoutValidation("Accept", "text/html");
            var page = await client.SendAsync(browser);
            page.StatusCode.Should().Be(HttpStatusCode.OK, because: $"{path} is a page, not an API path");
            (await page.Content.ReadAsStringAsync()).Should().Be("page:" + path.TrimStart('/'));
        }
    }

    [Fact]
    public async Task ModuleNotLoaded_JsonPostAnswersAnHonest404_NeverTheShell()
    {
        // The #2093 failure mode: the MeshWeaver.Mcp module never host-loaded, so nothing maps
        // /mcp. A protocol POST must answer 404 — the signal that made that outage findable —
        // and never a 200 HTML shell.
        await using var app = BuildApp(mapMcpEndpoint: false);
        await app.StartAsync();
        using var client = app.GetTestClient();

        foreach (var url in new[] { "/mcp", "/api/mcp" })
        {
            var response = await client.SendAsync(JsonPost(url));
            response.StatusCode.Should().Be(HttpStatusCode.NotFound,
                because: $"a JSON POST to {url} with no MCP endpoint mapped must fail loud");
        }

        // …while the partition page stays reachable for browsers even then.
        var browser = new HttpRequestMessage(HttpMethod.Get, "/Mcp");
        browser.Headers.TryAddWithoutValidation("Accept", "text/html");
        var page = await client.SendAsync(browser);
        (await page.Content.ReadAsStringAsync()).Should().Be("page:Mcp");
    }

    [Fact]
    public async Task ApiMcpPrefixIsSegmentExact_NeverRewritesNeighbours()
    {
        await using var app = BuildApp(mapMcpEndpoint: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.SendAsync(JsonPost("/api/mcpx"));

        (await response.Content.ReadAsStringAsync()).Should().NotBe(McpEndpointBody,
            because: "/api/mcpx is not the MCP endpoint; the rewrite matches whole segments");
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/api/mcp", "/api/mcp")]
    [InlineData("/.well-known/oauth-protected-resource/mcp", "/mcp")]
    [InlineData("/.well-known/oauth-protected-resource", "(bare)")]
    public async Task PathInsertedResourceMetadata_ReachesTheBareDocument_WithTheResourceStashed(
        string url, string expectedStash)
    {
        // RFC 9728: a strict MCP client derives the metadata URL from the endpoint path it
        // connects to. Both derivations must answer, each naming ITS resource; the bare
        // document (the URL the WWW-Authenticate challenge carries) keeps its default.
        await using var app = BuildApp(mapMcpEndpoint: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(expectedStash);
    }

    [Fact]
    public void AddMcpAuthentication_RegistersTheAliasStartupFilter()
    {
        // The alias is self-wired: the composition (plugins repo) calls AddMcpAuthentication and
        // nothing else — if this registration goes missing, /api/mcp silently stops being served
        // while every test above still passes on its explicit registration.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpAuthentication();

        services.Should().Contain(d =>
            d.ServiceType == typeof(IStartupFilter)
            && d.ImplementationType == typeof(McpEndpointRoutes.StartupFilter));
    }
}
