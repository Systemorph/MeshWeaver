using System;
using System.Collections.Generic;
using MeshWeaver.Blazor.Portal;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Frontend selection (Portal:Frontend / Portal:ReactAppUrl + the mw-frontend override cookie):
/// the deployment default picks the frontend, the per-user cookie overrides it, and only
/// interactive HTML page navigations are ever redirected to the React app — framework/static/
/// transport/auth paths and the toggle endpoint itself always pass through. Inert unless
/// Portal:ReactAppUrl is configured, so existing deployments are unaffected.
/// </summary>
public class FrontendSelectionTest
{
    private static IConfiguration Config(params (string Key, string Value)[] settings)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (key, value) in settings)
            dict[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IConfiguration Enabled(params (string Key, string Value)[] settings)
    {
        var all = new List<(string, string)> { (FrontendSelection.ReactAppUrlKey, "/app/") };
        all.AddRange(settings);
        return Config(all.ToArray());
    }

    private static DefaultHttpContext HtmlGet(string path, string? cookie = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Headers.Accept = "text/html,application/xhtml+xml";
        if (cookie != null)
            context.Request.Headers.Cookie = $"{FrontendSelection.CookieName}={cookie}";
        return context;
    }

    [Fact]
    public void EffectiveFrontend_DefaultsToBlazor()
    {
        FrontendSelection.EffectiveFrontend(HtmlGet("/"), Config())
            .Should().Be(FrontendSelection.Blazor);
    }

    [Fact]
    public void EffectiveFrontend_ReadsTheDeploymentDefault()
    {
        FrontendSelection.EffectiveFrontend(HtmlGet("/"), Config((FrontendSelection.FrontendKey, "React")))
            .Should().Be(FrontendSelection.React);
    }

    [Fact]
    public void CookieOverride_BeatsTheDeploymentDefault_BothDirections()
    {
        // Deployment says React, the user opted back to classic:
        FrontendSelection.EffectiveFrontend(
                HtmlGet("/", cookie: "Blazor"),
                Config((FrontendSelection.FrontendKey, "React")))
            .Should().Be(FrontendSelection.Blazor);

        // Deployment default (Blazor), the user opted into the new frontend:
        FrontendSelection.EffectiveFrontend(HtmlGet("/", cookie: "React"), Config())
            .Should().Be(FrontendSelection.React);
    }

    [Fact]
    public void WithoutReactAppUrl_TheFeatureIsInert()
    {
        FrontendSelection.IsEnabled(Config()).Should().BeFalse();
        FrontendSelection.ShouldRedirectToReact(
                HtmlGet("/", cookie: "React"),
                Config((FrontendSelection.FrontendKey, "React")))
            .Should().BeFalse(because: "no Portal:ReactAppUrl means no redirect, ever");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/ACME/Overview")]
    [InlineData("/User/Alice")]
    public void InteractiveNavigations_RedirectWhenReactIsEffective(string path)
    {
        FrontendSelection.ShouldRedirectToReact(HtmlGet(path, cookie: "React"), Enabled())
            .Should().BeTrue();
        FrontendSelection.ShouldRedirectToReact(HtmlGet(path), Enabled((FrontendSelection.FrontendKey, "React")))
            .Should().BeTrue(because: "the deployment default applies when there is no override");
        FrontendSelection.ShouldRedirectToReact(HtmlGet(path), Enabled())
            .Should().BeFalse(because: "Blazor is the default frontend");
    }

    [Theory]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/MeshWeaver.Blazor/css/app.css")]
    [InlineData("/mcp")]
    [InlineData("/api/mesh/upload")]
    [InlineData("/signalr")]
    [InlineData("/healthz")]
    [InlineData("/signin-oidc")]
    [InlineData("/frontend/react")]
    public void ExcludedPrefixes_NeverRedirect(string path)
    {
        FrontendSelection.ShouldRedirectToReact(HtmlGet(path, cookie: "React"), Enabled())
            .Should().BeFalse(because: $"'{path}' is not an interactive page navigation");
    }

    [Fact]
    public void AssetsNonGetAndNonHtml_NeverRedirect()
    {
        FrontendSelection.ShouldRedirectToReact(HtmlGet("/site.css", cookie: "React"), Enabled())
            .Should().BeFalse(because: "paths with an extension are assets");

        var post = HtmlGet("/", cookie: "React");
        post.Request.Method = "POST";
        FrontendSelection.ShouldRedirectToReact(post, Enabled())
            .Should().BeFalse(because: "only GET navigations are redirected");

        var fetch = HtmlGet("/", cookie: "React");
        fetch.Request.Headers.Accept = "application/json";
        FrontendSelection.ShouldRedirectToReact(fetch, Enabled())
            .Should().BeFalse(because: "XHR/fetch requests are not page navigations");
    }

    [Fact]
    public void RequestsAlreadyInsideTheReactApp_NeverRedirect()
    {
        FrontendSelection.ShouldRedirectToReact(HtmlGet("/app/threads", cookie: "React"), Enabled())
            .Should().BeFalse(because: "the React app itself must not bounce (redirect loop)");
    }
}
