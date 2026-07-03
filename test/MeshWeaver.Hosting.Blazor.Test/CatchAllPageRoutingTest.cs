using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Infrastructure;
using MeshWeaver.Blazor.Pages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// End-to-end regression pin for mesh-path routing through BOTH routing layers:
/// ASP.NET endpoint routing (which selects the razor-component endpoint) AND the
/// Blazor Router component (which picks the page during SSR and on every
/// interactive navigation).
///
/// Regression under test (Systemorph/agentic-pensions#10): a Document node URL whose
/// mesh path ends in a file extension (e.g. AgenticPension/content/_Documents/PKG_2026.pdf)
/// rendered "Sorry, there's nothing at this address". Root cause: the inline
/// ":nonfile" constraint in ApplicationPage's @page template was resolved by the
/// Blazor Router component against its FIXED, compiled-in constraint map
/// (int, bool, ..., file, nonfile) where "nonfile" is the built-in dot-rejecting
/// NonFileNameRouteConstraint. The DI ConstraintMap registration (which mapped
/// "nonfile" to the prefix-based MeshWeaver constraint) only ever reached the
/// endpoint-routing layer — the Router component cannot be customized, so every
/// path whose last segment contained a dot was rejected by the Router even though
/// the endpoint matched.
///
/// The fix removes the inline constraint from the page templates (the Router now
/// matches every mesh path) and re-applies the prefix-based static-asset exclusion
/// where it belongs and is customizable: as an endpoint convention
/// (ExcludeStaticAssetPaths) on the root catch-all endpoint, so requests under
/// _framework/, _content/ etc. still fall through to the static-file pipeline / 404
/// instead of being swallowed by the Blazor page.
/// </summary>
public class CatchAllPageRoutingTest : IAsyncLifetime
{
    private WebApplication? app;
    private HttpClient client = null!;
    private string webRoot = null!;

    private const string StaticAssetContent = "/* probe asset */";

    public async ValueTask InitializeAsync()
    {
        // A real webroot with a real asset, to pin that genuine static files keep
        // being served by the static-file middleware ahead of the Blazor catch-all.
        webRoot = Path.Combine(AppContext.BaseDirectory, "catchall-routing-webroot");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "probe-asset.css"), StaticAssetContent);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = webRoot,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddRazorComponents();

        var webApp = builder.Build();

        // Mirrors the portal pipeline (MemexConfiguration.StartPortalApplication):
        // static files BEFORE routing, then the razor-component endpoints with the
        // static-asset exclusion applied to the root catch-all page endpoint.
        webApp.UseStaticFiles();
        webApp.UseRouting();
        webApp.UseAntiforgery();
        webApp.MapRazorComponents<RouterProbeApp>()
            .AddAdditionalAssemblies(typeof(ApplicationPage).Assembly)
            .ExcludeStaticAssetPaths();

        await webApp.StartAsync(TestContext.Current.CancellationToken);
        app = webApp;
        client = webApp.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
            await app.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task DocumentPath_WithFileExtension_RoutesToApplicationPage()
    {
        // The exact URL shape from the issue: a Document node under a content
        // collection, whose slug ends in ".pdf".
        var (_, body) = await GetAsync("/AgenticPension/content/_Documents/PKG_2026.pdf");

        body.Should().NotContain(RouterProbeApp.NotFoundMarker,
            because: "a mesh-node path ending in a file extension must not be rejected by the Blazor Router");
        body.Should().Contain($"{RouterProbeApp.FoundMarker}{nameof(ApplicationPage)}",
            because: "document node URLs are mesh paths and must reach the catch-all application page");
    }

    [Fact]
    public async Task AreaPath_WithFileExtension_RoutesToAreaPage()
    {
        var (_, body) = await GetAsync("/area/AgenticPension/content/_Documents/PKG_2026.pdf");

        body.Should().Contain($"{RouterProbeApp.FoundMarker}{nameof(AreaPage)}",
            because: "area routes address mesh paths too and must allow file extensions");
    }

    [Fact]
    public async Task PlainApplicationPath_RoutesToApplicationPage()
    {
        var (_, body) = await GetAsync("/ACME/Overview");

        body.Should().Contain($"{RouterProbeApp.FoundMarker}{nameof(ApplicationPage)}");
    }

    [Theory]
    [InlineData("/_framework/does-not-exist.js")]
    [InlineData("/_content/Some.Lib/missing.css")]
    [InlineData("/favicon.ico")]
    [InlineData("/auth/login")]
    [InlineData("/signin-microsoft")]
    public async Task StaticAndInfrastructurePrefixes_AreNotSwallowedByCatchAllPage(string path)
    {
        var (status, body) = await GetAsync(path);

        body.Should().NotContain(RouterProbeApp.FoundMarker,
            because: $"'{path}' must not be swallowed by the Blazor catch-all page");
        status.Should().Be(HttpStatusCode.NotFound,
            because: $"'{path}' has no endpoint and no physical file, so it must fall through to 404");
    }

    [Fact]
    public async Task RealStaticAsset_IsServedByStaticFileMiddleware()
    {
        var (status, body) = await GetAsync("/probe-asset.css");

        status.Should().Be(HttpStatusCode.OK);
        body.Should().Be(StaticAssetContent,
            because: "physical static assets are served by the static-file middleware ahead of routing");
    }

}
