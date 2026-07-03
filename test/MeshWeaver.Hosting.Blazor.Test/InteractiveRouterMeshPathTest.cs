using System;
using System.Threading.Tasks;
using MeshWeaver.Blazor.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// Regression pin for Systemorph/agentic-pensions#10 at the layer where the bug lives:
/// the Blazor Router component's OWN route matching.
///
/// The portal renders &lt;Routes @rendermode="InteractiveServer" /&gt;. Inside the
/// interactive circuit (hydration and every subsequent navigation) there is no
/// endpoint-routing RouteData to fall back on, so the Router matches the URI against
/// its own route table, built by the Components-internal template parser. That parser
/// has a FIXED, compiled-in constraint map (int, bool, ..., file, nonfile) in which
/// "nonfile" is the built-in dot-rejecting NonFileNameRouteConstraint — the app-level
/// ConstraintMap registration that maps ":nonfile" to MeshWeaver's prefix-based
/// constraint NEVER reaches it. With ":nonfile" inline in ApplicationPage's @page
/// template, every mesh path whose last segment contains a dot (all Document nodes:
/// .pdf/.docx/.txt) was rejected by the Router and rendered
/// "Sorry, there's nothing at this address."
///
/// This harness renders the real Router through HtmlRenderer WITHOUT endpoint routing —
/// exactly the interactive router's matching path — so these tests fail (red) while the
/// inline ":nonfile" is in the template and pass once the templates carry no constraint.
/// </summary>
public class InteractiveRouterMeshPathTest
{
    private static async Task<string> RenderRouterAsync(string relativeUri)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new StaticNavigationManager(
            "https://portal.test/", "https://portal.test/" + relativeUri));
        services.AddSingleton<INavigationInterception, NoopNavigationInterception>();
        services.AddSingleton<IScrollToLocationHash, NoopScrollToLocationHash>();
        var provider = services.BuildServiceProvider();
        await using (provider)
        {
            var htmlRenderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            await using (htmlRenderer)
            {
                return await htmlRenderer.Dispatcher.InvokeAsync(async () =>
                {
                    var output = await htmlRenderer.RenderComponentAsync<RouterProbeApp>();
                    return output.ToHtmlString();
                });
            }
        }
    }

    [Fact]
    public async Task DocumentPath_WithFileExtension_IsRoutedToApplicationPage()
    {
        // The exact URL shape from the issue: a Document node under a content
        // collection, whose slug ends in ".pdf".
        var html = await RenderRouterAsync("AgenticPension/content/_Documents/PKG_2026.pdf");

        html.Should().NotContain(RouterProbeApp.NotFoundMarker,
            because: "a mesh-node path ending in a file extension must not be rejected by the interactive router");
        html.Should().Contain($"{RouterProbeApp.FoundMarker}{nameof(ApplicationPage)}",
            because: "document node URLs are mesh paths and must reach the catch-all application page");
    }

    [Fact]
    public async Task AreaPath_WithFileExtension_IsRoutedToAreaPage()
    {
        var html = await RenderRouterAsync("area/AgenticPension/content/_Documents/PKG_2026.pdf");

        html.Should().Contain($"{RouterProbeApp.FoundMarker}{nameof(AreaPage)}",
            because: "area routes address mesh paths too and must allow file extensions");
    }

    [Fact]
    public async Task PlainMeshPath_IsRoutedToApplicationPage()
    {
        var html = await RenderRouterAsync("ACME/Overview");

        html.Should().Contain($"{RouterProbeApp.FoundMarker}{nameof(ApplicationPage)}");
    }

    private sealed class StaticNavigationManager : NavigationManager
    {
        public StaticNavigationManager(string baseUri, string uri) => Initialize(baseUri, uri);

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
        }
    }

    private sealed class NoopNavigationInterception : INavigationInterception
    {
        public Task EnableNavigationInterceptionAsync() => Task.CompletedTask;
    }

    private sealed class NoopScrollToLocationHash : IScrollToLocationHash
    {
        public Task RefreshScrollPositionForHash(string locationAbsolute) => Task.CompletedTask;
    }
}
