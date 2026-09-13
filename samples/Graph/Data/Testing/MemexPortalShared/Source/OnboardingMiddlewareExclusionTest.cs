// <meshweaver>
// Id: Testing/MemexPortalShared/OnboardingMiddlewareExclusionTest
// DisplayName: Testing/MemexPortalShared/OnboardingMiddlewareExclusionTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

public class OnboardingMiddlewareExclusionTest
{
    private static ClaimsPrincipal AuthenticatedUser =>
        new(new ClaimsIdentity("TestAuth"));

    [MeshTheory]
    [MeshInlineData("/login")]
    [MeshInlineData("/auth/callback")]
    [MeshInlineData("/_framework/blazor.js")]
    [MeshInlineData("/_content/MeshWeaver.Blazor/css/app.css")]
    [MeshInlineData("/static/img.png")]
    [MeshInlineData("/favicon.ico")]
    [MeshInlineData("/mcp")]
    [MeshInlineData("/api/mcp")]
    [MeshInlineData("/signin-microsoft")]
    public async Task ExcludedPrefixes_SkipOnboardingCheck(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = AuthenticatedUser;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: $"'{path}' should be excluded and pass through to next");
    }

    [MeshTheory]
    [MeshInlineData("/signin-microsoft")]
    [MeshInlineData("/signin-google")]
    [MeshInlineData("/signin-oidc")]
    public async Task SigninCallbackPaths_AreExcluded(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = AuthenticatedUser;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: $"'{path}' should be excluded by the /signin- prefix");
    }

    [MeshTheory]
    [MeshInlineData("/ACME/Overview")]
    [MeshInlineData("/User/Alice")]
    [MeshInlineData("/")]
    public async Task NonExcludedPaths_AttemptOnboardingCheck(string path)
    {
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = AuthenticatedUser;

        // Non-excluded paths with authenticated user will attempt to resolve
        // PortalApplication from DI. Since RequestServices isn't set up, this throws.
        var act = () => middleware.InvokeAsync(context);
        await act.Should().ThrowAsync<Exception>(
            because: "non-excluded paths should attempt onboarding check via PortalApplication");
    }

    [MeshFact]
    public async Task OnboardingPage_IsResolved_NotBlanketExcluded()
    {
        // /onboarding is no longer blanket-excluded: it participates in the onboarded
        // check so an ALREADY-onboarded user landing there gets redirected home. With no
        // PortalApplication wired into RequestServices the resolution attempt surfaces
        // (throws) — proving the path is no longer short-circuited to pass-through.
        RequestDelegate next = _ => Task.CompletedTask;

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/onboarding";
        context.User = AuthenticatedUser;

        var act = () => middleware.InvokeAsync(context);
        await act.Should().ThrowAsync<Exception>(
            because: "/onboarding now participates in the onboarded check to redirect onboarded users home");
    }

    [MeshTheory]
    [MeshInlineData("/_FRAMEWORK/blazor.js")]
    [MeshInlineData("/LOGIN")]
    [MeshInlineData("/STATIC/img.png")]
    public async Task ExcludedPrefixes_AreCaseInsensitive(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.User = AuthenticatedUser;

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(because: "exclusion should be case-insensitive");
    }

    [MeshTheory]
    [MeshInlineData("/ACME/Overview")]
    [MeshInlineData("/User/Alice")]
    [MeshInlineData("/")]
    public async Task UnauthenticatedUser_SkipsEntireCheck(string path)
    {
        var nextCalled = false;
        RequestDelegate next = _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new OnboardingMiddleware(next, NullLogger<OnboardingMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        // No user set — unauthenticated

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue(
            because: "unauthenticated users should skip the entire onboarding check");
    }
}
